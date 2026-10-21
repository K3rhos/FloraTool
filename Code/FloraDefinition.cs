using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.FloraTool;

/// <summary>
/// One kind of flora the brush can plant. Weight decides how often it comes up relative to the
/// other entries in the definition.
/// </summary>
public sealed class FloraEntry
{
	[Property]
	public Model Model { get; set; }

	/// <summary>Relative chance of this entry being picked. Zero excludes it without deleting it.</summary>
	[Property, Range(0, 10)]
	public float Weight { get; set; } = 1.0f;

	[Property]
	public RangedFloat Scale { get; set; } = new(0.85f, 1.25f);

	/// <summary>Random spin about the vertical axis, so repeated instances don't read as clones.</summary>
	[Property]
	public bool RandomYaw { get; set; } = true;

	/// <summary>
	/// Tilts the instance toward the surface normal. Right for rocks and bushes, usually wrong for
	/// trees - a trunk growing perpendicular to a hillside looks broken.
	/// </summary>
	[Property, Range(0, 1)]
	public float AlignToNormal { get; set; } = 0.0f;

	/// <summary>Random lean away from vertical, in degrees. A little goes a long way on trees.</summary>
	[Property, Range(0, 45)]
	public float RandomTilt { get; set; } = 0.0f;

	/// <summary>Sinks the instance into the ground, hiding the seam where the base meets the surface.</summary>
	[Property, Range(0, 64)]
	public float SinkDepth { get; set; } = 0.0f;

	/// <summary>
	/// Gives this entry real collision. Colliders are only created near the player, so this is about
	/// whether the flora is solid at all - not about paying for every painted instance at once.
	/// </summary>
	[Property, Group("Physics")]
	public bool EnablePhysics { get; set; } = true;

	[Property, Group("Rendering")]
	public bool CastShadows { get; set; } = true;

	public bool HasModel => Model is not null && !string.IsNullOrEmpty(Model.ResourcePath);
}

/// <summary>
/// A palette of flora plus the rules used when painting it. Shared by every
/// <see cref="FloraRenderer"/> that references it, so a whole world can be retuned from one asset.
/// </summary>
[AssetType(Name = "Flora Definition", Extension = "floradef", Category = "Flora")]
public sealed class FloraDefinition : GameResource
{
	[Property]
	public List<FloraEntry> Entries { get; set; } = [];

	/// <summary>
	/// Instances a fully painted cell can hold. Coverage scales this, so it sets the ceiling on how
	/// tightly flora can pack - raise it for undergrowth, leave it low for trees.
	/// </summary>
	[Property, Group("Painting"), Range(1, 16)]
	public int MaxPerCell { get; set; } = 2;

	/// <summary>Minimum ground normal Z. Steeper than this and nothing plants, so cliffs stay bare.</summary>
	[Property, Group("Painting"), Range(0, 1)]
	public float SlopeLimit { get; set; } = 0.6f;

	/// <summary>
	/// Radius around the viewer within which chunks are turned into scene objects. Chunks beyond it
	/// keep their painted coverage but cost nothing to render.
	/// </summary>
	[Property, Group("Streaming"), Range(2000, 100000)]
	public float StreamRadius { get; set; } = 25000.0f;

	/// <summary>
	/// Distance past which flora stops casting shadows. Shadow cascades ignore the view frustum, so
	/// distant trees are rendered into them whichever way the camera faces - dropping them is one of
	/// the few savings that applies even when you are looking away.
	///
	/// Set it too low and you will see shadows wink out as chunks cross the boundary, most obviously
	/// under a low sun where far geometry casts long shadows into view. Zero disables the cutoff.
	/// </summary>
	[Property, Group("Streaming"), Range(0, 50000)]
	public float ShadowDistance { get; set; } = 10000.0f;

	/// <summary>
	/// Radius around the viewer within which entries flagged <see cref="FloraEntry.EnablePhysics"/>
	/// get real colliders. Keep it just past where the player can reach.
	/// </summary>
	[Property, Group("Physics"), Range(256, 20000)]
	public float CollisionRadius { get; set; } = 4000.0f;

	/// <summary>
	/// The entry at an index, or null when the index no longer resolves - entries can be removed
	/// after coverage has already been painted naming them.
	/// </summary>
	public FloraEntry GetEntry(int index)
	{
		if (Entries is null || index < 0 || index >= Entries.Count)
			return null;

		var entry = Entries[index];
		return entry?.HasModel is true ? entry : null;
	}
}
