using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.FloraTool;

/// <summary>
/// Collision for painted flora. Instances only exist as scene objects, so nothing is solid until a
/// collider is made for it - and those are made only for instances near the viewer and recycled as
/// it moves, keeping physics cost tied to what is reachable rather than to the whole forest.
/// </summary>
public sealed partial class FloraRenderer
{
	private readonly record struct CollisionKey(FloraStorage.ChunkCoord Chunk, int Index);

	private readonly Dictionary<CollisionKey, GameObject> _colliders = [];

	// A set rather than a list: SyncColliders tests every live collider against it, so a linear scan
	// there would be quadratic once a few hundred are in range.
	private readonly HashSet<CollisionKey> _wantedColliders = [];
	private readonly List<CollisionKey> _staleColliders = [];

	private GameObject _collisionRoot;
	private Vector3 _lastCollisionOrigin;
	private bool _hasCollisionOrigin;
	private int _collisionRevision = -1;

	/// <summary>
	/// Rebuilding walks every streamed instance, so it only happens once the viewer has moved far
	/// enough for the answer to have changed.
	/// </summary>
	private const float CollisionRefreshDistance = 256.0f;

	private void UpdateCollision(Vector3 origin)
	{
		if (!Definition.IsValid() || Definition.CollisionRadius <= 0.0f)
		{
			ReleaseCollision();
			return;
		}

		var storageChanged = Storage is null || _collisionRevision != Storage.Revision;

		if (!storageChanged && _hasCollisionOrigin &&
			 origin.Distance(_lastCollisionOrigin) < CollisionRefreshDistance)
			return;

		_collisionRevision = Storage?.Revision ?? -1;
		_lastCollisionOrigin = origin;
		_hasCollisionOrigin = true;

		GatherWantedColliders(origin);
		SyncColliders();
	}

	/// <summary>
	/// Only streamed chunks are considered. Collision radius should sit well inside the stream radius
	/// anyway, so anything outside it has no business being solid.
	/// </summary>
	private void GatherWantedColliders(Vector3 origin)
	{
		_wantedColliders.Clear();

		var radiusSquared = Definition.CollisionRadius * Definition.CollisionRadius;

		foreach (var (coord, chunk) in _live)
		{
			for (var i = 0; i < chunk.Instances.Count; i++)
			{
				var instance = chunk.Instances[i];

				if (instance.Position.DistanceSquared(origin) > radiusSquared)
					continue;

				var entry = Definition.GetEntry(instance.EntryIndex);
				if (entry?.EnablePhysics is not true)
					continue;

				_wantedColliders.Add(new CollisionKey(coord, i));
			}
		}
	}

	private void SyncColliders()
	{
		// Drop what fell out of range first, so those objects are free to be reused this same frame.
		_staleColliders.Clear();

		foreach (var (key, gameObject) in _colliders)
		{
			if (gameObject.IsValid() && _wantedColliders.Contains(key))
				continue;

			_staleColliders.Add(key);
		}

		foreach (var key in _staleColliders)
		{
			if (_colliders.Remove(key, out var gameObject) && gameObject.IsValid())
				gameObject.Destroy();
		}

		foreach (var key in _wantedColliders)
		{
			if (_colliders.ContainsKey(key))
				continue;

			var gameObject = CreateCollider(key);
			if (gameObject.IsValid())
				_colliders[key] = gameObject;
		}
	}

	private GameObject CreateCollider(CollisionKey key)
	{
		if (!_live.TryGetValue(key.Chunk, out var chunk))
			return null;

		if (key.Index < 0 || key.Index >= chunk.Instances.Count)
			return null;

		var instance = chunk.Instances[key.Index];

		var entry = Definition.GetEntry(instance.EntryIndex);
		if (entry is null)
			return null;

		EnsureCollisionRoot();

		var gameObject = new GameObject(true, "FloraCollider")
		{
			Parent = _collisionRoot,
			WorldTransform = instance.ToTransform(),
		};

		// Not saved with the scene and not shown in the hierarchy - these are transient physics
		// proxies for geometry that is regenerated from the seed anyway.
		gameObject.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;

		var collider = gameObject.Components.Create<ModelCollider>();
		collider.Model = entry.Model;
		collider.Static = true;

		return gameObject;
	}

	private void EnsureCollisionRoot()
	{
		if (_collisionRoot.IsValid())
			return;

		_collisionRoot = new GameObject(true, "Flora Colliders") { Parent = GameObject };
		_collisionRoot.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
	}

	/// <summary>
	/// Drops every collider belonging to one chunk, for when that chunk is about to be regenerated
	/// and its instance indices renumbered.
	/// </summary>
	private void ReleaseChunkColliders( FloraStorage.ChunkCoord coord )
	{
		if ( _colliders.Count == 0 )
			return;

		_staleColliders.Clear();

		foreach ( var (key, _) in _colliders )
		{
			if ( key.Chunk == coord )
				_staleColliders.Add( key );
		}

		foreach ( var key in _staleColliders )
		{
			if ( _colliders.Remove( key, out var gameObject ) && gameObject.IsValid() )
				gameObject.Destroy();
		}

		_staleColliders.Clear();
	}

	private void ReleaseCollision()
	{
		foreach (var (_, gameObject) in _colliders)
		{
			if (gameObject.IsValid())
				gameObject.Destroy();
		}

		_colliders.Clear();
		_wantedColliders.Clear();
		_staleColliders.Clear();

		if (_collisionRoot.IsValid())
			_collisionRoot.Destroy();

		_collisionRoot = null;
		_hasCollisionOrigin = false;
		_collisionRevision = -1;
	}
}
