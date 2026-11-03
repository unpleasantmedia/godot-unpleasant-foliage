#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage.Editor;

/// <summary>Shows a type's allowed textures in the Inspector as a tick for each of the ground's textures.</summary>
[Tool]
public partial class FoliageTypeInspector : EditorInspectorPlugin
{
    public override bool _CanHandle(GodotObject @object) => @object is FoliageType;

    public override bool _ParseProperty(GodotObject @object, Variant.Type type, string name, PropertyHint hintType, string hintString, PropertyUsageFlags usageFlags, bool wide)
    {
        if (name != FoliageType.PropertyName.AllowedTextures.ToString())
            return false;
        AddPropertyEditor(name, new AllowedTexturesProperty(TextureNames()));
        return true;
    }

    /// <summary>
    /// The textures of the ground of the field in the Inspector, or else the first in the scene, or null
    /// if the scene has none: a type being edited has no field of its own, and the Inspector draws
    /// a field's types before the plugin is told it is selected. Found here each time, as the
    /// editor makes this object anew when the scripts are rebuilt.
    /// </summary>
    private static IReadOnlyList<string>? TextureNames()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var field = EditorInterface.Singleton.GetInspector().GetEditedObject() as FoliageField
            ?? (root is null ? null : FoliageField.InTree(root.GetTree()).FirstOrDefault(f => root.IsAncestorOf(f)));
        return field is { } found && found.IsInsideTree() ? found.Ground()?.TextureNames ?? [] : null;
    }
}

/// <summary>The ticks: one for each texture of the ground, and one for any name ticked that the ground no longer has.</summary>
[Tool]
public partial class AllowedTexturesProperty : EditorProperty
{
    private readonly IReadOnlyList<string>? _textures;
    private readonly VBoxContainer _ticks = new();
    private readonly Label _note = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Dictionary<string, CheckBox> _boxes = [];

    public AllowedTexturesProperty()
    {
    }

    public AllowedTexturesProperty(IReadOnlyList<string>? textures)
    {
        _textures = textures;
        var column = new VBoxContainer();
        column.AddChild(_note);
        column.AddChild(_ticks);
        AddChild(column);
        SetBottomEditor(column);
    }

    public override void _UpdateProperty()
    {
        var allowed = GetEditedObject().Get(GetEditedProperty()).AsStringArray();
        var known = _textures ?? [];
        _note.Text = _textures is null ? "Open a scene with a FoliageField to choose from its ground's textures."
            : known.Count == 0 ? "This ground has no textures to choose from."
            : "Allowed only where nothing but ticked textures shows, so in a fade only between them. None ticked: allowed on any.";
        var wanted = known.Concat(allowed.Except(known)).ToList();
        if (!wanted.SequenceEqual(_boxes.Keys))
        {
            foreach (var child in _ticks.GetChildren())
                child.Free();
            _boxes.Clear();
            foreach (var name in wanted)
            {
                var box = new CheckBox { Text = known.Contains(name) ? name : $"{name} (not on this ground)" };
                box.Toggled += on => Ticked(on, name);
                _ticks.AddChild(box);
                AddFocusable(box);
                _boxes[name] = box;
            }
        }

        foreach (var (name, box) in _boxes)
            box.SetPressedNoSignal(allowed.Contains(name));
    }

    private void Ticked(bool on, string name)
    {
        var allowed = GetEditedObject().Get(GetEditedProperty()).AsStringArray().Where(a => a != name).ToList();
        if (on)
            allowed.Add(name);
        EmitChanged(GetEditedProperty(), allowed.ToArray());
    }
}
#endif
