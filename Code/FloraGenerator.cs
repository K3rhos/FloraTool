using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.FloraTool;

/// <summary>
/// Turns painted coverage into concrete instances. Everything here is a pure function of the chunk
/// coordinate, the cell contents and the seed - no state, no RNG object - so a chunk regenerates
/// identically every run, on every machine, however many times it is streamed in and out.
/// </summary>
public static class FloraGenerator
{
	public readonly record struct Instance(int EntryIndex, Vector3 Position, Rotation Rotation, float Scale)
	{
		public readonly Transform ToTransform() => new(Position, Rotation, Scale);
	}

	/// <summary>
	/// Integer avalanche hash. Deterministic across runs and platforms, which the framework RNG is
	/// not guaranteed to be, and cheap enough to call several times per instance.
	/// </summary>
	private static uint Hash(uint x)
	{
		x ^= x >> 16;
		x *= 0x7feb352du;
		x ^= x >> 15;
		x *= 0x846ca68bu;
		x ^= x >> 16;
		return x;
	}

	private static float HashFloat(uint x) => Hash(x) * (1.0f / 4294967296.0f);

	/// <summary>
	/// Generates every instance for one chunk, appending into <paramref name="results"/>.
	/// </summary>
	public static void GenerateChunk(FloraStorage.ChunkCoord coord, FloraStorage.Cell[] cells,
		FloraDefinition definition, int seed, List<Instance> results)
	{
		if (cells is null || definition is null)
			return;

		var origin = FloraStorage.ChunkOrigin(coord);
		var maxPerCell = Math.Max(definition.MaxPerCell, 1);

		// Mixing the chunk coordinate into the seed keeps neighbouring chunks from sharing a
		// sequence, which would otherwise show up as a visible repeating pattern across the world.
		var chunkSeed = Hash((uint)seed
			^ Hash((uint)coord.X * 73856093u)
			^ Hash((uint)coord.Y * 19349663u));

		for (var cellIndex = 0; cellIndex < cells.Length; cellIndex++)
		{
			var cell = cells[cellIndex];

			var density = cell.Density;
			if (density <= 0.0f)
				continue;

			if (cell.Normal.z < definition.SlopeLimit)
				continue;

			var cellSeed = Hash(chunkSeed ^ Hash((uint)cellIndex * 0x9e3779b9u));

			var cx = cellIndex % FloraStorage.ChunkResolution;
			var cy = cellIndex / FloraStorage.ChunkResolution;

			var cellMinX = origin.x + cx * FloraStorage.CellSize;
			var cellMinY = origin.y + cy * FloraStorage.CellSize;

			// Fractional counts are resolved by a hash rather than rounding, so density reads as a
			// smooth thinning across a field instead of stepping between whole numbers per cell.
			var exact = density * maxPerCell;
			var count = (int)exact;
			if (HashFloat(cellSeed ^ 0x1b56c4e9u) < exact - count)
				count++;

			for (var i = 0; i < count; i++)
			{
				var s = Hash(cellSeed + (uint)i * 0x85ebca6bu);

				var entry = ResolveEntry(definition, cell.EntryIndex, s);
				if (entry.Index < 0)
					continue;

				results.Add(BuildInstance(entry.Index, entry.Entry, cell, s, cellMinX, cellMinY));
			}
		}
	}

	/// <summary>
	/// A cell either names its entry - painted deliberately with one species selected - or defers to
	/// the definition's weights.
	/// </summary>
	private static (int Index, FloraEntry Entry) ResolveEntry(FloraDefinition definition, int cellEntryIndex, uint seed)
	{
		var entries = definition.Entries;
		if (entries is null || entries.Count == 0)
			return (-1, null);

		if (cellEntryIndex < entries.Count)
		{
			var named = entries[cellEntryIndex];
			return named?.HasModel is true ? (cellEntryIndex, named) : (-1, null);
		}

		var total = 0.0f;
		for (var i = 0; i < entries.Count; i++)
		{
			if (entries[i]?.HasModel is true && entries[i].Weight > 0.0f)
				total += entries[i].Weight;
		}

		if (total <= 0.0f)
			return (-1, null);

		var pick = HashFloat(seed ^ 0x3c6ef372u) * total;

		for (var i = 0; i < entries.Count; i++)
		{
			var entry = entries[i];
			if (entry?.HasModel is not true || entry.Weight <= 0.0f)
				continue;

			pick -= entry.Weight;
			if (pick <= 0.0f)
				return (i, entry);
		}

		return (-1, null);
	}

	private static Instance BuildInstance(int entryIndex, FloraEntry entry, FloraStorage.Cell cell,
		uint seed, float cellMinX, float cellMinY)
	{
		var jitterX = HashFloat(seed ^ 0x68bc21ebu);
		var jitterY = HashFloat(seed ^ 0x02e5be93u);

		var x = cellMinX + jitterX * FloraStorage.CellSize;
		var y = cellMinY + jitterY * FloraStorage.CellSize;

		var normal = cell.Normal;

		// The baked height is the cell centre's, so a slope needs the offset carried across to the
		// jittered position or trunks float on the uphill side and sink on the downhill one.
		var offsetX = x - (cellMinX + FloraStorage.CellSize * 0.5f);
		var offsetY = y - (cellMinY + FloraStorage.CellSize * 0.5f);
		var z = cell.Height - (normal.x * offsetX + normal.y * offsetY) / MathF.Max(normal.z, 0.1f);

		var position = new Vector3(x, y, z);
		if (entry.SinkDepth > 0.0f)
			position -= normal * entry.SinkDepth;

		var rotation = entry.RandomYaw
			? Rotation.FromYaw(HashFloat(seed ^ 0x7f4a7c15u) * 360.0f)
			: Rotation.Identity;

		if (entry.AlignToNormal > 0.0f)
		{
			var aligned = Rotation.LookAt(normal) * Rotation.FromPitch(90.0f);
			rotation = Rotation.Slerp(rotation, aligned * rotation, entry.AlignToNormal);
		}

		if (entry.RandomTilt > 0.0f)
		{
			var tiltAngle = HashFloat(seed ^ 0x165667b1u) * entry.RandomTilt;
			var tiltDirection = HashFloat(seed ^ 0x27d4eb2fu) * 360.0f;
			rotation *= Rotation.FromAxis(Rotation.FromYaw(tiltDirection).Forward, tiltAngle);
		}

		var scale = MathX.Lerp(entry.Scale.Min, entry.Scale.Max, HashFloat(seed ^ 0xd3a2646cu));

		return new Instance(entryIndex, position, rotation, scale);
	}
}
