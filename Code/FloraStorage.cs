using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.FloraTool;

/// <summary>
/// Painted flora coverage, stored as a sparse chunked grid of density samples rather than one
/// transform per tree. Instances are regenerated from this plus a seed, so a forest of a hundred
/// thousand trees costs a few megabytes instead of tens - which matters because the scene sidecar
/// has to survive being committed to a repository.
///
/// The trade is that positions are derived, not authored: painting decides where flora *can* grow
/// and how densely, and the seed decides exactly where each trunk lands.
/// </summary>
public sealed class FloraStorage : BlobData
{
	public override int Version => 1;

	/// <summary>Cells along one edge of a chunk.</summary>
	public const int ChunkResolution = 32;

	/// <summary>
	/// World size of one density cell. Roughly a tree's footprint - each cell holds at most a
	/// handful of instances, so this is what bounds how tightly flora can pack.
	/// Changing it invalidates every painted scene, so it is a constant rather than a setting.
	/// </summary>
	public const float CellSize = 256.0f;

	public const float ChunkSize = ChunkResolution * CellSize;

	public const int CellsPerChunk = ChunkResolution * ChunkResolution;

	/// <summary>
	/// One coverage sample. Height and normal are baked at paint time so flora sits on whatever
	/// geometry was there, without the renderer having to trace anything at load.
	/// </summary>
	public struct Cell
	{
		public float Height;

		/// <summary>density (0-7) | normal.x (8-15) | normal.y (16-23) | entry index (24-31)</summary>
		public uint Packed;

		public readonly float Density => (Packed & 0xFF) / 255.0f;

		/// <summary>Index into the definition's entry list. 0xFF means "pick one by weight".</summary>
		public readonly int EntryIndex => (int)((Packed >> 24) & 0xFF);

		public readonly Vector3 Normal
		{
			get
			{
				var x = ((Packed >> 8) & 0xFF) / 127.5f - 1.0f;
				var y = ((Packed >> 16) & 0xFF) / 127.5f - 1.0f;
				var z = MathF.Sqrt(Math.Clamp(1.0f - x * x - y * y, 0.0f, 1.0f));
				return new Vector3(x, y, z);
			}
		}

		public static uint Pack(float density, Vector3 normal, int entryIndex)
		{
			var d = (uint)Math.Clamp(density * 255.0f + 0.5f, 0.0f, 255.0f);
			var nx = (uint)Math.Clamp((normal.x + 1.0f) * 127.5f + 0.5f, 0.0f, 255.0f);
			var ny = (uint)Math.Clamp((normal.y + 1.0f) * 127.5f + 0.5f, 0.0f, 255.0f);
			var e = (uint)Math.Clamp(entryIndex, 0, 255);

			return d | (nx << 8) | (ny << 16) | (e << 24);
		}
	}

	public readonly record struct ChunkCoord(int X, int Y);

	private readonly Dictionary<ChunkCoord, Cell[]> _chunks = [];

	/// <summary>
	/// Chunks changed since the renderer last looked. Without this a single painted cell is
	/// indistinguishable from the whole map changing, and the renderer rebuilds every streamed chunk
	/// for one brush dab.
	/// </summary>
	private readonly HashSet<ChunkCoord> _dirtyChunks = [];

	/// <summary>Chunks touched since the last <see cref="ConsumeDirtyChunks"/>.</summary>
	public IReadOnlyCollection<ChunkCoord> DirtyChunks => _dirtyChunks;

	/// <summary>
	/// True when the change was broad enough that tracking individual chunks is pointless - a clear,
	/// or a fresh deserialize. The renderer rebuilds everything in that case.
	/// </summary>
	public bool EverythingDirty { get; private set; } = true;

	public void ConsumeDirtyChunks()
	{
		_dirtyChunks.Clear();
		EverythingDirty = false;
	}

	private void MarkChunkDirty( ChunkCoord coord )
	{
		_dirtyChunks.Add( coord );
		Revision++;
	}

	private void MarkEverythingDirty()
	{
		_dirtyChunks.Clear();
		EverythingDirty = true;
		Revision++;
	}

	/// <summary>Bumped on every mutation so the renderer knows to regenerate.</summary>
	public int Revision { get; private set; }

	public int ChunkCount => _chunks.Count;

	public IReadOnlyDictionary<ChunkCoord, Cell[]> Chunks => _chunks;

	public static ChunkCoord WorldToChunk(Vector3 world) => new(
		(int)MathF.Floor(world.x / ChunkSize),
		(int)MathF.Floor(world.y / ChunkSize));

	public static Vector2 ChunkOrigin(ChunkCoord coord) => new(coord.X * ChunkSize, coord.Y * ChunkSize);

	public static Vector3 ChunkCenter(ChunkCoord coord, float height = 0.0f)
	{
		var origin = ChunkOrigin(coord);
		return new Vector3(origin.x + ChunkSize * 0.5f, origin.y + ChunkSize * 0.5f, height);
	}

	private static int WorldToCell(float world) => (int)MathF.Floor(world / CellSize);

	private static int FloorDiv(int a, int b) => a >= 0 ? a / b : ~(~a / b);

	private static int Mod(int a, int b)
	{
		var r = a % b;
		return r < 0 ? r + b : r;
	}

	/// <summary>
	/// Writes a coverage sample, baking the surface height and normal alongside it. Density of zero
	/// frees the sample.
	/// </summary>
	public void SetCell(float worldX, float worldY, float density, float height, Vector3 normal, int entryIndex)
	{
		var cellX = WorldToCell(worldX);
		var cellY = WorldToCell(worldY);
		var coord = new ChunkCoord(FloorDiv(cellX, ChunkResolution), FloorDiv(cellY, ChunkResolution));

		if (!_chunks.TryGetValue(coord, out var cells))
		{
			if (density <= 0.0f) return;

			cells = new Cell[CellsPerChunk];
			_chunks[coord] = cells;
		}

		var index = Mod(cellY, ChunkResolution) * ChunkResolution + Mod(cellX, ChunkResolution);
		var cell = new Cell { Height = height, Packed = Cell.Pack(density, normal, entryIndex) };

		// Dragging the brush over coverage that is already saturated rewrites the same values every
		// frame. Bailing here keeps that from dirtying the chunk and rebuilding it for nothing.
		if (cells[index].Height == cell.Height && cells[index].Packed == cell.Packed)
			return;

		cells[index] = cell;

		MarkChunkDirty(coord);
	}

	public Cell GetCell(float worldX, float worldY)
	{
		var cellX = WorldToCell(worldX);
		var cellY = WorldToCell(worldY);
		var coord = new ChunkCoord(FloorDiv(cellX, ChunkResolution), FloorDiv(cellY, ChunkResolution));

		if (!_chunks.TryGetValue(coord, out var cells))
			return default;

		return cells[Mod(cellY, ChunkResolution) * ChunkResolution + Mod(cellX, ChunkResolution)];
	}

	/// <summary>Reduces coverage in a radius, removing samples that reach zero.</summary>
	public void Erase(Vector3 center, float radius, float strength)
	{
		var radiusSquared = radius * radius;

		var minCellX = WorldToCell(center.x - radius);
		var maxCellX = WorldToCell(center.x + radius);
		var minCellY = WorldToCell(center.y - radius);
		var maxCellY = WorldToCell(center.y + radius);

		var changed = false;

		for (var cy = minCellY; cy <= maxCellY; cy++)
		{
			for (var cx = minCellX; cx <= maxCellX; cx++)
			{
				var coord = new ChunkCoord(FloorDiv(cx, ChunkResolution), FloorDiv(cy, ChunkResolution));
				if (!_chunks.TryGetValue(coord, out var cells))
					continue;

				var wx = (cx + 0.5f) * CellSize;
				var wy = (cy + 0.5f) * CellSize;
				var dx = wx - center.x;
				var dy = wy - center.y;

				if (dx * dx + dy * dy > radiusSquared)
					continue;

				var index = Mod(cy, ChunkResolution) * ChunkResolution + Mod(cx, ChunkResolution);
				ref var cell = ref cells[index];

				if ((cell.Packed & 0xFF) == 0)
					continue;

				var density = Math.Max(cell.Density - strength, 0.0f);
				cell.Packed = density <= 0.0f
					? 0u
					: Cell.Pack(density, cell.Normal, cell.EntryIndex);

				MarkChunkDirty(coord);
				changed = true;
			}
		}

		if (!changed)
			return;

		PruneEmptyChunks();
	}

	public void ClearAll()
	{
		if (_chunks.Count == 0) return;

		_chunks.Clear();
		MarkEverythingDirty();
	}

	private void PruneEmptyChunks()
	{
		List<ChunkCoord> empty = null;

		foreach (var (coord, cells) in _chunks)
		{
			var used = false;
			for (var i = 0; i < cells.Length; i++)
			{
				if ((cells[i].Packed & 0xFF) != 0) { used = true; break; }
			}

			if (!used)
			{
				empty ??= [];
				empty.Add(coord);
			}
		}

		if (empty is null) return;

		foreach (var coord in empty)
			_chunks.Remove(coord);
	}

	/// <summary>
	/// Writes only the painted cells. Storing them densely cost 8KB per chunk however little of it
	/// was painted, and a brush stroke across a landscape touches a lot of chunks.
	///
	/// Each painted cell costs 2 bytes more than it did dense (its index), so a chunk past about 80%
	/// coverage is cheaper stored densely. Both layouts are written and each chunk says which it used.
	/// </summary>
	public override void Serialize(ref Writer writer)
	{
		writer.Stream.Write(_chunks.Count);

		foreach (var (coord, cells) in _chunks)
		{
			writer.Stream.Write(coord.X);
			writer.Stream.Write(coord.Y);

			var painted = 0;
			for (var i = 0; i < CellsPerChunk; i++)
			{
				if ((cells[i].Packed & 0xFF) != 0) painted++;
			}

			var sparse = painted * 10 < CellsPerChunk * 8;
			writer.Stream.Write(sparse);

			if (!sparse)
			{
				for (var i = 0; i < CellsPerChunk; i++)
				{
					writer.Stream.Write(cells[i].Height);
					writer.Stream.Write(cells[i].Packed);
				}

				continue;
			}

			writer.Stream.Write(painted);

			for (var i = 0; i < CellsPerChunk; i++)
			{
				if ((cells[i].Packed & 0xFF) == 0)
					continue;

				writer.Stream.Write((ushort)i);
				writer.Stream.Write(cells[i].Height);
				writer.Stream.Write(cells[i].Packed);
			}
		}
	}

	public override void Deserialize(ref Reader reader)
	{
		_chunks.Clear();

		var chunkCount = reader.Stream.Read<int>();

		for (var c = 0; c < chunkCount; c++)
		{
			var coord = new ChunkCoord(reader.Stream.Read<int>(), reader.Stream.Read<int>());
			var cells = new Cell[CellsPerChunk];

			if (reader.Stream.Read<bool>())
			{
				var painted = reader.Stream.Read<int>();

				for (var p = 0; p < painted; p++)
				{
					var index = reader.Stream.Read<ushort>();
					var height = reader.Stream.Read<float>();
					var packed = reader.Stream.Read<uint>();

					if (index < CellsPerChunk)
					{
						cells[index].Height = height;
						cells[index].Packed = packed;
					}
				}
			}
			else
			{
				for (var i = 0; i < CellsPerChunk; i++)
				{
					cells[i].Height = reader.Stream.Read<float>();
					cells[i].Packed = reader.Stream.Read<uint>();
				}
			}

			_chunks[coord] = cells;
		}

		MarkEverythingDirty();
	}
}
