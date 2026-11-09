using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// The foliage there is to paint with, each a <see cref="FoliageType"/>: one library for a whole
/// project (Project Settings &gt; Unpleasant Foliage &gt; Library), or one given to a
/// <see cref="FoliageField"/> of its own.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_foliage/Icons/Foliage.svg")]
public partial class FoliageLibrary : Resource
{
    /// <summary>The project setting naming the project's library.</summary>
    public const string Setting = "unpleasant_foliage/library";

    private Godot.Collections.Array<FoliageType> _types = [];

    [Export]
    public Godot.Collections.Array<FoliageType> Types
    {
        get => _types;
        set
        {
            _types = value ?? [];
            EmitChanged();
        }
    }

    /// <summary>The project's library, as Project Settings name it; null if none is set or it cannot be loaded.</summary>
    public static FoliageLibrary? ProjectLibrary() =>
        ProjectSettings.GetSetting(Setting, "").AsString() is { Length: > 0 } path && ResourceLoader.Exists(path)
            ? ResourceLoader.Load<FoliageLibrary>(path)
            : null;
}
