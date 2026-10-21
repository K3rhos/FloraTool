using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.FloraTool;

/// <summary>
/// Renders painted flora. Coverage is stored per chunk and instances are regenerated from it plus
/// <see cref="Seed"/>, so the scene file holds a density map rather than a transform per tree - the
/// difference between a few megabytes and something a repository will refuse.
///
/// Chunks become scene objects only within the definition's stream radius. Scene objects rather than
/// a hand-rolled instanced draw because they take part in every pass the engine runs: the depth
/// prepass, the shadow cascades, and per-object LOD using the model's own compiled distances.
/// Standard instancing still batches them into few draw calls.
/// </summary>
[Icon("park"), Group("Flora"), Title("Flora Renderer")]
public sealed partial class FloraRenderer : Component, Component.ExecuteInEditor, Component.DontExecuteOnServer
{
	/// <summary>A chunk's generated instances and the scene objects currently standing for them.</summary>
	private sealed class LiveChunk
	{
		public List<FloraGenerator.Instance> Instances = [];
		public List<SceneObject> SceneObjects = [];

		/// <summary>
		/// Whether this chunk is currently allowed to cast. Tracked so the flags are only touched
		/// when a chunk crosses the shadow boundary, rather than every object every frame.
		/// </summary>
		public bool ShadowsEnabled = true;
	}

	[Property, Group("General")]
	public FloraDefinition Definition { get; set; }

	/// <summary>
	/// Decides exactly where each instance lands within the painted coverage. Change it to reshuffle
	/// a whole forest without repainting; keep it fixed and the same trees stand in the same places
	/// every run, on every machine.
	/// </summary>
	[Property, Group("General")]
	public int Seed
	{
		get => field;
		set
		{
			if (field == value) return;
			field = value;

			// Every instance in the world moves, so this is the one edit that really does invalidate
			// every chunk.
			_fullRebuild = true;
			MarkDirty();
		}
	}

	/// <summary>Painted coverage. Serialized as a binary blob, not JSON.</summary>
	[Property, Hide]
	public FloraStorage Storage { get; set; } = new();

	private readonly Dictionary<FloraStorage.ChunkCoord, LiveChunk> _live = [];
	private readonly List<FloraStorage.ChunkCoord> _wantedChunks = [];
	private readonly List<FloraStorage.ChunkCoord> _staleChunks = [];

	// Reused across chunk builds so streaming doesn't allocate a fresh list per chunk.
	private readonly List<FloraGenerator.Instance> _scratchInstances = [];

	private int _builtRevision = -1;

	/// <summary>
	/// Set when a change invalidates every chunk rather than a few - reseeding, or coming back from
	/// disabled. Painting never sets this; the storage's dirty set covers it.
	/// </summary>
	private bool _fullRebuild = true;
	private Vector3 _lastStreamOrigin;
	private bool _hasStreamOrigin;

	/// <summary>
	/// Restreaming walks every painted chunk, so it only happens once the viewer has moved far enough
	/// for the answer to have changed. A fraction of a chunk keeps the boundary from thrashing.
	/// </summary>
	private const float StreamRefreshDistance = FloraStorage.ChunkSize * 0.25f;

	protected override void OnEnabled()
	{
		Storage ??= new FloraStorage();

		// Scene objects were deleted on disable, so a matching revision would leave us thinking the
		// world is already built when nothing is in it.
		_builtRevision = -1;
		_hasStreamOrigin = false;
		_fullRebuild = true;
	}

	protected override void OnDisabled()
	{
		ReleaseAllChunks();
		ReleaseCollision();

		_builtRevision = -1;
		_hasStreamOrigin = false;
	}

	protected override void OnUpdate()
	{
		var viewer = GetViewerPosition();
		if (!viewer.HasValue)
			return;

		UpdateStreaming(viewer.Value);
		UpdateCollision(viewer.Value);
	}

	/// <summary>
	/// What streaming follows. While editing that is the viewport camera, so flora appears around
	/// what you are looking at rather than wherever the game camera is parked.
	/// </summary>
	private Vector3? GetViewerPosition()
	{
		if (Scene.IsEditor)
		{
			var editorCamera = Application.Editor?.Camera;
			if (editorCamera.IsValid())
				return editorCamera.WorldPosition;
		}

		return Scene.Camera.IsValid() ? Scene.Camera.WorldPosition : null;
	}

	private void UpdateStreaming(Vector3 origin)
	{
		if (Storage is null || !Definition.IsValid())
		{
			ReleaseAllChunks();
			return;
		}

		// A brush stroke touches a cell or two, so only the chunks holding them are rebuilt. Releasing
		// everything on any change meant one dab destroyed and recreated every scene object inside
		// the stream radius - tens of thousands of them, every frame of the drag.
		var dirty = _builtRevision != Storage.Revision;

		if (!dirty && _hasStreamOrigin && origin.Distance(_lastStreamOrigin) < StreamRefreshDistance)
			return;

		if (dirty)
		{
			if (_fullRebuild || Storage.EverythingDirty)
			{
				ReleaseAllChunks();
			}
			else
			{
				// Dropping them here is enough - SyncChunks rebuilds any that are still in range.
				foreach (var coord in Storage.DirtyChunks)
					ReleaseChunk(coord);
			}

			Storage.ConsumeDirtyChunks();

			_fullRebuild = false;
			_builtRevision = Storage.Revision;
		}

		_lastStreamOrigin = origin;
		_hasStreamOrigin = true;

		GatherWantedChunks(origin);
		SyncChunks(origin);
	}

	private void GatherWantedChunks(Vector3 origin)
	{
		_wantedChunks.Clear();

		// A chunk's near corner can be in range while its centre is not, hence the circumradius.
		var radius = Definition.StreamRadius + FloraStorage.ChunkSize * 0.7072f;
		var radiusSquared = radius * radius;

		foreach (var (coord, _) in Storage.Chunks)
		{
			var center = FloraStorage.ChunkCenter(coord);

			var dx = center.x - origin.x;
			var dy = center.y - origin.y;

			if (dx * dx + dy * dy > radiusSquared)
				continue;

			_wantedChunks.Add(coord);
		}
	}

	private void SyncChunks(Vector3 origin)
	{
		_staleChunks.Clear();

		foreach (var (coord, _) in _live)
		{
			if (!_wantedChunks.Contains(coord))
				_staleChunks.Add(coord);
		}

		foreach (var coord in _staleChunks)
			ReleaseChunk(coord);

		foreach (var coord in _wantedChunks)
		{
			if (_live.ContainsKey(coord))
				continue;

			BuildChunk(coord, origin);
		}

		UpdateChunkShadows(origin);
	}

	/// <summary>
	/// Turns shadow casting off for chunks past the shadow distance. Evaluated per chunk rather than
	/// per instance, and only written when a chunk actually crosses the boundary, so a stationary
	/// camera costs nothing here.
	/// </summary>
	private void UpdateChunkShadows(Vector3 origin)
	{
		foreach (var (coord, chunk) in _live)
		{
			var wanted = ChunkCastsShadows(coord, origin);
			if (wanted == chunk.ShadowsEnabled)
				continue;

			chunk.ShadowsEnabled = wanted;
			ApplyChunkShadows(chunk);
		}
	}

	private bool ChunkCastsShadows(FloraStorage.ChunkCoord coord, Vector3 origin)
	{
		var distance = Definition.ShadowDistance;
		if (distance <= 0.0f)
			return true;

		// Measured to the chunk's near edge via its circumradius, so a chunk is only cut off once all
		// of it is beyond the limit.
		var limit = distance + FloraStorage.ChunkSize * 0.7072f;

		var center = FloraStorage.ChunkCenter(coord);
		var dx = center.x - origin.x;
		var dy = center.y - origin.y;

		return dx * dx + dy * dy <= limit * limit;
	}

	/// <summary>
	/// The per-entry CastShadows setting is the ceiling - distance can only ever take shadows away,
	/// never grant them to an entry the artist turned them off for.
	/// </summary>
	private void ApplyChunkShadows(LiveChunk chunk)
	{
		for (var i = 0; i < chunk.SceneObjects.Count && i < chunk.Instances.Count; i++)
		{
			var sceneObject = chunk.SceneObjects[i];
			if (!sceneObject.IsValid())
				continue;

			var entry = Definition.GetEntry(chunk.Instances[i].EntryIndex);
			sceneObject.Flags.CastShadows = chunk.ShadowsEnabled && entry?.CastShadows is true;
		}
	}

	private void BuildChunk(FloraStorage.ChunkCoord coord, Vector3 origin)
	{
		if (!Storage.Chunks.TryGetValue(coord, out var cells))
			return;

		var world = Scene.SceneWorld;
		if (!world.IsValid())
			return;

		var chunk = new LiveChunk();
		chunk.ShadowsEnabled = ChunkCastsShadows(coord, origin);

		_scratchInstances.Clear();
		FloraGenerator.GenerateChunk(coord, cells, Definition, Seed, _scratchInstances);

		// Instances and scene objects are kept strictly parallel - anything whose entry no longer
		// resolves is dropped from both. Skipping only the scene object would slide the two lists out
		// of step, and the shadow and collision paths index one by the other.
		for (var i = 0; i < _scratchInstances.Count; i++)
		{
			var instance = _scratchInstances[i];

			var entry = Definition.GetEntry(instance.EntryIndex);
			if (entry is null)
				continue;

			var sceneObject = new SceneObject(world, entry.Model, instance.ToTransform());
			sceneObject.Flags.CastShadows = chunk.ShadowsEnabled && entry.CastShadows;

			chunk.Instances.Add(instance);
			chunk.SceneObjects.Add(sceneObject);
		}

		_live[coord] = chunk;
	}

	private void ReleaseChunk(FloraStorage.ChunkCoord coord)
	{
		if (!_live.Remove(coord, out var chunk))
			return;

		foreach (var sceneObject in chunk.SceneObjects)
		{
			if (sceneObject.IsValid())
				sceneObject.Delete();
		}

		chunk.SceneObjects.Clear();
		chunk.Instances.Clear();

		// Colliders are keyed by instance index within the chunk, and regenerating it renumbers
		// those - adding coverage in one cell shifts every instance after it. A key that happened to
		// survive would then be holding a collider for a different tree, at the old transform.
		ReleaseChunkColliders(coord);
	}

	private void ReleaseAllChunks()
	{
		foreach (var (_, chunk) in _live)
		{
			foreach (var sceneObject in chunk.SceneObjects)
			{
				if (sceneObject.IsValid())
					sceneObject.Delete();
			}
		}

		_live.Clear();
		_wantedChunks.Clear();
		_staleChunks.Clear();
	}

	/// <summary>
	/// Called by the editor tool after painting, so the next frame regenerates. Also fires when the
	/// seed changes.
	/// </summary>
	/// <summary>
	/// Called by the editor tool after painting. Deliberately does not force a full rebuild - the
	/// storage already recorded which chunks the stroke touched, and only those are regenerated.
	/// </summary>
	public void MarkDirty()
	{
		_builtRevision = -1;
		_hasStreamOrigin = false;
	}

	/// <summary>Total instances currently streamed in. Useful when tuning density and stream radius.</summary>
	[Property, ReadOnly] public int LiveInstanceCount
	{
		get
		{
			var count = 0;
			foreach (var (_, chunk) in _live)
				count += chunk.SceneObjects.Count;
			return count;
		}
	}

	protected override void DrawGizmos()
	{
		if (!Gizmo.IsSelected || Storage is null || Storage.ChunkCount == 0)
			return;

		Gizmo.Draw.Color = Color.Green.WithAlpha(0.25f);

		foreach (var (coord, _) in Storage.Chunks)
		{
			var origin = FloraStorage.ChunkOrigin(coord);

			var mins = WorldTransform.PointToLocal(new Vector3(origin.x, origin.y, 0));
			var maxs = WorldTransform.PointToLocal(new Vector3(
				origin.x + FloraStorage.ChunkSize, origin.y + FloraStorage.ChunkSize, 0));

			Gizmo.Draw.LineBBox(new BBox(mins, maxs));
		}
	}
}
