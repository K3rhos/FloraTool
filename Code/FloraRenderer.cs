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

	// A set, not a list: SyncChunks asks it about every live chunk, and a linear scan inside that loop
	// is quadratic in the number of chunks streamed in.
	private readonly HashSet<FloraStorage.ChunkCoord> _wantedChunks = [];
	private readonly List<FloraStorage.ChunkCoord> _staleChunks = [];

	/// <summary>What is wanted but not yet standing, nearest first.</summary>
	private readonly List<(float DistanceSquared, FloraStorage.ChunkCoord Coord)> _buildQueue = [];

	/// <summary>
	/// Scene objects a released chunk gave up, waiting to be handed to the next one built.
	///
	/// Making them is quick; destroying thousands at once is not, and that is the stall when you walk
	/// out of a forest rather than into one. A pooled object is hidden rather than deleted, and comes
	/// back with a new model and transform - the model setter does nothing at all when it is the same
	/// species, which it usually is.
	/// </summary>
	private readonly List<SceneObject> _pool = [];

	/// <summary>Most spare objects destroyed in one frame, so trimming never becomes the stall.</summary>
	private const int TrimPerFrame = 256;

	/// <summary>
	/// Set when the frame budget ran out with chunks still to build, so streaming keeps running next
	/// frame rather than waiting for the viewer to move far enough to be asked again.
	/// </summary>
	private bool _streamIncomplete;

	/// <summary>
	/// How much further out a chunk is released than it is built. A chunk sitting exactly on the line
	/// would otherwise be destroyed and rebuilt as the camera jitters across it, and rebuilding one
	/// means recreating every scene object in it.
	/// </summary>
	private const float StreamHysteresis = 1.15f;

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

		// Nothing left to hand them to. One bulk delete when the component goes is a cost worth
		// paying; the pool exists to keep it out of ordinary streaming, not to outlive it.
		foreach (var sceneObject in _pool)
		{
			if (sceneObject.IsValid())
				sceneObject.Delete();
		}

		_pool.Clear();

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

		// Outside UpdateStreaming on purpose - that returns early while the viewer is still, and a
		// pool left holding a forest's worth of hidden objects is the memory this is meant to bound.
		TrimPool();
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

		if (!dirty && !_streamIncomplete && _hasStreamOrigin && origin.Distance(_lastStreamOrigin) < StreamRefreshDistance)
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
		_buildQueue.Clear();

		// A chunk's near corner can be in range while its centre is not, hence the circumradius.
		var build = Definition.StreamRadius + FloraStorage.ChunkSize * 0.7072f;
		var buildSquared = build * build;

		var keep = build * StreamHysteresis;
		var keepSquared = keep * keep;

		void Consider(FloraStorage.ChunkCoord coord)
		{
			var center = FloraStorage.ChunkCenter(coord);

			var dx = center.x - origin.x;
			var dy = center.y - origin.y;
			var distanceSquared = dx * dx + dy * dy;

			if (distanceSquared > keepSquared)
				return;

			var live = _live.ContainsKey(coord);

			// Only what is already standing gets the further radius; nothing new starts out there.
			if (!live && distanceSquared > buildSquared)
				return;

			_wantedChunks.Add(coord);

			if (!live)
				_buildQueue.Add((distanceSquared, coord));
		}

		// Walking every painted chunk costs the whole map however little of it is in range, and
		// walking the square around the viewer costs the same however little of it is painted. Take
		// whichever is the smaller of the two.
		var span = (int)MathF.Ceiling(keep / FloraStorage.ChunkSize) + 1;
		var across = 2 * span + 1;

		if (across * across < Storage.Chunks.Count)
		{
			var middle = FloraStorage.WorldToChunk(origin);

			for (var y = -span; y <= span; y++)
			{
				for (var x = -span; x <= span; x++)
				{
					var coord = new FloraStorage.ChunkCoord(middle.X + x, middle.Y + y);

					if (Storage.Chunks.ContainsKey(coord))
						Consider(coord);
				}
			}
		}
		else
		{
			foreach (var (coord, _) in Storage.Chunks)
				Consider(coord);
		}

		// Nearest first, so a frame that runs out of time has spent it on what is closest.
		_buildQueue.Sort(static (first, second) => first.DistanceSquared.CompareTo(second.DistanceSquared));
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

		// Built nearest first, and only for as long as the budget allows. Streaming into a dense
		// forest otherwise builds every newly wanted chunk in one frame - thousands of scene objects
		// at once, and a visible stall every time the viewer crosses a chunk.
		var started = RealTime.NowDouble;
		var budget = Definition.BuildBudget;

		_streamIncomplete = false;

		foreach (var (_, coord) in _buildQueue)
		{
			if (_live.ContainsKey(coord))
				continue;

			BuildChunk(coord, origin);

			if ((RealTime.NowDouble - started) * 1000.0 <= budget)
				continue;

			// What is left stays wanted, so nothing releases it; the flag is what brings streaming
			// back next frame, since the viewer may not have moved far enough to ask again.
			_streamIncomplete = true;
			break;
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

			var sceneObject = Rent(world, entry.Model, instance.ToTransform());
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
			Return(sceneObject);

		chunk.SceneObjects.Clear();
		chunk.Instances.Clear();

		// Colliders are keyed by instance index within the chunk, and regenerating it renumbers
		// those - adding coverage in one cell shifts every instance after it. A key that happened to
		// survive would then be holding a collider for a different tree, at the old transform.
		ReleaseChunkColliders(coord);
	}

	/// <summary>
	/// A spare scene object set to this model and transform, or a new one when there are none. Taken
	/// from the back, so the ones handed out are the ones most recently given up - those are the most
	/// likely to still be the same species, and the model setter then does nothing.
	/// </summary>
	private SceneObject Rent(SceneWorld world, Model model, Transform transform)
	{
		while (_pool.Count > 0)
		{
			var pooled = _pool[^1];
			_pool.RemoveAt(_pool.Count - 1);

			if (!pooled.IsValid())
				continue;

			pooled.Model = model;
			pooled.Transform = transform;
			pooled.RenderingEnabled = true;

			return pooled;
		}

		return new SceneObject(world, model, transform);
	}

	private void Return(SceneObject sceneObject)
	{
		if (!sceneObject.IsValid())
			return;

		sceneObject.RenderingEnabled = false;
		_pool.Add(sceneObject);
	}

	/// <summary>
	/// Lets the pool back down when it is holding far more than the forest is using - walking away
	/// from a dense area hands back everything at once. A few at a time, because deleting them in
	/// bulk is exactly the stall the pool exists to avoid.
	/// </summary>
	private void TrimPool()
	{
		var keep = Math.Max(LiveInstanceCount, 1024);

		for (var i = 0; i < TrimPerFrame && _pool.Count > keep; i++)
		{
			var spare = _pool[^1];
			_pool.RemoveAt(_pool.Count - 1);

			if (spare.IsValid())
				spare.Delete();
		}
	}

	private void ReleaseAllChunks()
	{
		foreach (var (_, chunk) in _live)
		{
			foreach (var sceneObject in chunk.SceneObjects)
				Return(sceneObject);
		}

		_live.Clear();
		_wantedChunks.Clear();
		_staleChunks.Clear();

		// The queue named chunks that no longer exist, and a half-finished pass over it means nothing
		// once everything has gone.
		_buildQueue.Clear();
		_streamIncomplete = false;
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
