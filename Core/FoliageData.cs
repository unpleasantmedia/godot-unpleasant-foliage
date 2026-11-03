using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// Where a <see cref="FoliageField"/>'s foliage is painted, kept in a file of its own
/// (Level-Foliage.res): for each <see cref="FoliageType"/> (by its file), every place one stands, as twelve
/// numbers apiece (a transform's basis, then its origin).
/// </summary>
[Tool]
[GlobalClass]
public partial class FoliageData : Resource
{
    /// <summary>Each type's file, and the places of its foliage.</summary>
    [Export]
    public Godot.Collections.Dictionary<string, float[]> Placed { get; set; } = new();
}
