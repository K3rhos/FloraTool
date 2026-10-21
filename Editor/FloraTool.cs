using System;
using System.Linq;
using Editor;
using Editor.TerrainEditor;
using Sandbox;

namespace RedSnail.FloraTool.Editor;

/// <summary>
/// Paints flora onto any surface. Each stroke scatters entries from the target renderer's
/// definition, honouring its spacing and slope rules, and bakes the resulting transform into the
/// renderer's storage. Hold Ctrl to erase.
/// </summary>
[EditorTool("flora")]
[Title("Flora")]
[Icon("park")]
public sealed class FloraPaintTool : EditorTool
{
	public BrushSettings BrushSettings { get; private set; } = new();

	private FloraRenderer _target;
	private bool _erasing;
	private bool _dragging;
	private bool _painted;
	private Vector3 _lastPaintPosition;

	private ComboBox _entryDropdown;

	/// <summary>Index into the definition's entries, or 255 for "mix by weight".</summary>
	private int _entryIndex = MixedEntryIndex;

	private const int MixedEntryIndex = 255;

	// The brush has to travel a fraction of its own radius before depositing again, or holding the
	// mouse still would keep hammering the same spot with traces.
	private float PaintStepDistance => BrushSettings.Size * 0.35f;

	public FloraPaintTool()
	{
		RebuildSidebarOnSelectionChange = false;
	}

	public override Widget CreateToolSidebar()
	{
		var sidebar = new ToolSidebarWidget();
		sidebar.AddTitle("Flora Brush", "brush");
		sidebar.MinimumWidth = 300;

		{
			var group = sidebar.AddGroup("Brush");
			var so = BrushSettings.GetSerialized();
			group.Add(ControlSheet.CreateRow(so.GetProperty(nameof(BrushSettings.Size))));
			group.Add(ControlSheet.CreateRow(so.GetProperty(nameof(BrushSettings.Opacity))));
		}

		{
			// Coverage names the entry it was painted with, so an artist can lay down pines here and
			// oaks there rather than getting one weighted mix everywhere.
			var group = sidebar.AddGroup("Entry");

			_entryDropdown = new ComboBox(sidebar);
			_entryDropdown.ToolTip = "Which flora entry this stroke paints. Mixed uses the definition's weights.";
			RebuildEntryOptions();

			group.Add(_entryDropdown);
		}

		{
			var group = sidebar.AddGroup("Actions");

			var clear = new Button("Clear All Flora", "delete_sweep");
			clear.ToolTip = "Remove every painted instance from the target Flora Renderer";
			clear.Clicked += () =>
			{
				var target = ResolveTarget();
				if (!target.IsValid() || target.Storage is null)
					return;

				// Wiping the whole painted set has no undo, so this one asks first.
				Dialog.AskConfirm(
					() =>
					{
						target.Storage.ClearAll();
						target.MarkDirty();
					},
					"Are you sure you want to delete all flora? This action cannot be undone.",
					"Delete All Flora",
					"Delete",
					"Cancel");
			};
			group.Add(clear);
		}

		sidebar.Layout.AddStretchCell();

		return sidebar;
	}

	public override void OnUpdate()
	{
		_erasing = Gizmo.IsCtrlPressed;

		DrawBrushPreview();

		Gizmo.Hitbox.BBox(BBox.FromPositionAndSize(Vector3.Zero, 999999));

		if (Gizmo.IsLeftMouseDown)
		{
			if (!_dragging)
			{
				_dragging = true;
				_lastPaintPosition = Vector3.Zero;
			}

			OnPaintUpdate();
		}
		else if (_dragging)
		{
			_dragging = false;
			_lastPaintPosition = Vector3.Zero;

			if (_painted)
			{
				ResolveTarget()?.MarkDirty();
				_painted = false;
			}
		}
	}

	/// <summary>
	/// Uses the selected renderer when there is one, otherwise the last used, otherwise the only one
	/// in the scene. Creating one implicitly would leave stray components behind every time someone
	/// opens the tool.
	/// </summary>
	private FloraRenderer ResolveTarget()
	{
		var selected = Selection
			.OfType<GameObject>()
			.Select(go => go.Components.Get<FloraRenderer>(FindMode.EnabledInSelfAndDescendants))
			.FirstOrDefault(r => r.IsValid());

		if (selected.IsValid())
		{
			_target = selected;
			return _target;
		}

		if (_target.IsValid())
			return _target;

		_target = Scene.GetAllComponents<FloraRenderer>().FirstOrDefault();
		return _target;
	}

	private void OnPaintUpdate()
	{
		var target = ResolveTarget();
		if (!target.IsValid() || target.Storage is null || !target.Definition.IsValid())
			return;

		var cursor = TraceCursor();
		if (!cursor.Hit)
			return;

		if (_lastPaintPosition != Vector3.Zero &&
			 Vector3.DistanceBetween(cursor.HitPosition, _lastPaintPosition) < PaintStepDistance)
			return;

		_lastPaintPosition = cursor.HitPosition;

		var radius = (float)BrushSettings.Size;
		var strength = BrushSettings.Opacity;

		if (_erasing)
		{
			target.Storage.Erase(cursor.HitPosition, radius, strength);
			_painted = true;
			return;
		}

		PaintCoverage(target, cursor.HitPosition, radius, strength);
		_painted = true;
	}

	/// <summary>
	/// Walks every coverage cell the brush touches and traces straight down onto the world, baking
	/// the surface height and normal so instances sit on whatever geometry is there. Nothing is
	/// placed here - the renderer derives the actual trunks from this coverage plus its seed.
	/// </summary>
	private void PaintCoverage(FloraRenderer target, Vector3 center, float radius, float strength)
	{
		var definition = target.Definition;
		var storage = target.Storage;

		var radiusSquared = radius * radius;

		var minX = (int)MathF.Floor((center.x - radius) / FloraStorage.CellSize);
		var maxX = (int)MathF.Floor((center.x + radius) / FloraStorage.CellSize);
		var minY = (int)MathF.Floor((center.y - radius) / FloraStorage.CellSize);
		var maxY = (int)MathF.Floor((center.y + radius) / FloraStorage.CellSize);

		// Enough headroom to find the surface from above without punching through overhangs the
		// brush was never aimed at.
		var traceHeight = radius + 2048.0f;

		for (var cy = minY; cy <= maxY; cy++)
		{
			for (var cx = minX; cx <= maxX; cx++)
			{
				var wx = (cx + 0.5f) * FloraStorage.CellSize;
				var wy = (cy + 0.5f) * FloraStorage.CellSize;

				var dx = wx - center.x;
				var dy = wy - center.y;
				var distSq = dx * dx + dy * dy;

				if (distSq > radiusSquared)
					continue;

				var from = new Vector3(wx, wy, center.z + traceHeight);
				var to = new Vector3(wx, wy, center.z - traceHeight);

				var tr = Scene.Trace.Ray(from, to)
					.UseRenderMeshes(true)
					.WithTag("solid")
					.Run();

				if (!tr.Hit)
					continue;

				if (tr.Normal.z < definition.SlopeLimit)
					continue;

				// Soft edge, so overlapping strokes build up smoothly instead of leaving a disc.
				var falloff = 1.0f - MathF.Sqrt(distSq) / radius;
				var added = strength * MathF.Pow(falloff, 0.5f);

				var existing = storage.GetCell(wx, wy).Density;
				var density = Math.Clamp(existing + added, 0.0f, 1.0f);

				storage.SetCell(wx, wy, density, tr.HitPosition.z, tr.Normal, _entryIndex);
			}
		}
	}

	/// <summary>
	/// Fills the entry dropdown from the target definition. Rebuilt on demand, since entries can be
	/// added or changed while the tool is open.
	/// </summary>
	private void RebuildEntryOptions()
	{
		if (_entryDropdown is null)
			return;

		_entryDropdown.Clear();
		_entryDropdown.AddItem("Mixed (by weight)", "shuffle", () => _entryIndex = MixedEntryIndex);

		var definition = ResolveTarget()?.Definition;
		if (!definition.IsValid() || definition.Entries is null)
			return;

		for (var i = 0; i < definition.Entries.Count; i++)
		{
			var entry = definition.Entries[i];
			if (entry?.HasModel is not true)
				continue;

			var index = i;
			var name = System.IO.Path.GetFileNameWithoutExtension(entry.Model.ResourcePath);

			_entryDropdown.AddItem(name, "park", () => _entryIndex = index);
		}
	}

	private SceneTraceResult TraceCursor() =>
		Scene.Trace.Ray(Gizmo.CurrentRay, 100000)
			.UseRenderMeshes(true)
			.WithTag("solid")
			.Run();

	private void DrawBrushPreview()
	{
		var tr = TraceCursor();
		if (!tr.Hit)
			return;

		using (Gizmo.Scope("FloraBrush"))
		{
			Gizmo.Draw.Color = _erasing
				? Color.FromBytes(250, 150, 150)
				: Color.FromBytes(160, 230, 150);

			Gizmo.Draw.LineCircle(tr.HitPosition + tr.Normal * 1.0f, tr.Normal, BrushSettings.Size);
			Gizmo.Draw.LineCircle(tr.HitPosition + tr.Normal * 1.0f, tr.Normal, BrushSettings.Size * 0.5f);
		}
	}
}
