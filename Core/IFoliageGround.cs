using System;
using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// The ground foliage is painted on and stands on. The plugin knows no terrain itself: a project
/// connects its own through <see cref="FoliageGround.Finder"/>.
/// </summary>
public interface IFoliageGround
{
    /// <summary>How high the ground is at a place, or NaN where there is none.</summary>
    float HeightAt(Vector3 at);

    /// <summary>Which way is square to the ground at a place, or NaN where there is none.</summary>
    Vector3 NormalAt(Vector3 at);

    /// <summary>Where a ray first meets the ground, if it does.</summary>
    Vector3? Intersect(Vector3 from, Vector3 direction);

    /// <summary>The ground's colour at a place, for foliage coloured to match it.</summary>
    Color ColourAt(Vector3 at);

    /// <summary>The names of the ground's textures, for a type to pick those it may be painted on; none if it has no such thing.</summary>
    IReadOnlyList<string> TextureNames { get; }

    /// <summary>How much of what is seen of the ground at a place is any of these textures (by name), from 0 to 1, or null if it can't say.</summary>
    float? ShareOf(Vector3 at, IReadOnlyCollection<string> textures);

    /// <summary>Has a callable called with the area (a Rect2, in world X and Z) whenever the ground changes there, if it isn't already.</summary>
    void Follow(Callable changed);
}

/// <summary>What a project tells the plugin.</summary>
public static class FoliageGround
{
    /// <summary>Gives the ground for a field, or null if there is none.</summary>
    public static Func<FoliageField, IFoliageGround?>? Finder { get; set; }

    /// <summary>Where new data files go; if not set, beside the level.</summary>
    public static string? DataFolder { get; set; }
}
