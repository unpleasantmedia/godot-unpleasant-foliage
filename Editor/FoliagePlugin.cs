#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage.Editor;

/// <summary>
/// Paints foliage in the 3D editor. With a <see cref="FoliageField"/> selected, the Foliage dock
/// lists the types of its library (or the project's), a section for each category, with
/// thumbnails, to pick which to paint or take away, and the viewport's
/// toolbar has the tools: Paint or Erase, how big the brush is, and how thick to paint. Drag on the
/// ground to paint (or erase); ctrl-drag erases whatever the tool. Each stroke is one step to undo.
/// </summary>
[Tool]
public partial class FoliagePlugin : EditorPlugin
{
    private HBoxContainer? _toolbar;
    private Button? _paintButton;
    private Button? _eraseButton;
    private SpinBox? _radius;
    private SpinBox? _density;
    private CheckBox? _eraseAll;
    private VBoxContainer? _dock;
    private Label? _hint;

    /// <summary>The categories, one after another: a heading and a list of its types, each with a thumbnail and its name under it.</summary>
    private VBoxContainer? _sections;
    private readonly List<ItemList> _lists = [];

    /// <summary>The types shown, by the number each list item holds.</summary>
    private readonly List<FoliageType> _shown = [];

    /// <summary>The library shown, watched for changes (to it, or to its types).</summary>
    private FoliageLibrary? _library;

    private FoliageField? _field;
    private FoliageTypeInspector? _inspector;
    private Vector3? _brushAt;
    private Camera3D? _camera;
    private bool _stroking;
    private bool _erasing;
    private Vector3 _lastDab;
    private Godot.Collections.Dictionary<string, float[]>? _before;
    private readonly Random _random = new();

    public override void _EnterTree()
    {
        _toolbar = new HBoxContainer();
        _toolbar.AddChild(new VSeparator());
        var tools = new ButtonGroup();
        _paintButton = new Button { Text = "Paint Foliage", ToggleMode = true, Flat = true, ButtonGroup = tools, TooltipText =
            "Drag on the ground to paint the foliage picked in the Foliage dock; ctrl-drag to erase." };
        _eraseButton = new Button { Text = "Erase Foliage", ToggleMode = true, Flat = true, ButtonGroup = tools, TooltipText =
            "Drag on the ground to take away the foliage picked in the Foliage dock (or all of it, with All ticked)." };
        tools.AllowUnpress = true;
        // By method name, not a lambda: the editor can then let go of them as it reloads the scripts.
        _paintButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.ToolToggled));
        _eraseButton.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.ToolToggled));
        Iconify(_paintButton, "Paint");
        Iconify(_eraseButton, "Erase");
        _toolbar.AddChild(_paintButton);
        _toolbar.AddChild(_eraseButton);
        _toolbar.AddChild(new Label { Text = "Radius" });
        _radius = new SpinBox { MinValue = 0.5, MaxValue = 100, Step = 0.5, Value = 5, Suffix = "m", TooltipText = "How big the brush is." };
        _toolbar.AddChild(_radius);
        _toolbar.AddChild(new Label { Text = "Density" });
        _density = new SpinBox { MinValue = 0.05, MaxValue = 4, Step = 0.05, Value = 1, Suffix = "×", TooltipText = "How thickly to paint, as a share of each type's own density." };
        _toolbar.AddChild(_density);
        _eraseAll = new CheckBox { Text = "All", TooltipText = "Erase every type, not only those picked in the Foliage dock." };
        _toolbar.AddChild(_eraseAll);
        _toolbar.Visible = false;
        ToolButtons.StyleAll(_toolbar);
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
        _inspector = new FoliageTypeInspector();
        AddInspectorPlugin(_inspector);

        AddLibrarySetting();
        _dock = new VBoxContainer { Name = "Foliage" };
        _hint = new Label { Text = "Select a FoliageField to paint with its types.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(_hint);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _sections = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_sections);
        _dock.AddChild(scroll);
        ProjectSettings.Singleton.Connect(ProjectSettings.SignalName.SettingsChanged, new Callable(this, MethodName.ShowTypes));
        // Below the Inspector, so both show while the field is being edited.
        // In its slot only while the plugin is shown: see _MakeVisible.
        EditorInterface.Singleton.GetInspector().Connect(EditorInspector.SignalName.PropertyEdited, new Callable(this, MethodName.PropertyEdited));
    }

    // Its library changed in the Inspector: listed again.
    private void PropertyEdited(string property)
    {
        if (_field is not null && property == FoliageField.PropertyName.Library.ToString())
            ShowTypes();
    }

    /// <summary>The project's library setting, shown in Project Settings (without Advanced Settings on), picked as a file.</summary>
    private static void AddLibrarySetting()
    {
        if (!ProjectSettings.HasSetting(FoliageLibrary.Setting))
            ProjectSettings.SetSetting(FoliageLibrary.Setting, "");
        ProjectSettings.SetInitialValue(FoliageLibrary.Setting, "");
        ProjectSettings.SetAsBasic(FoliageLibrary.Setting, true);
        ProjectSettings.AddPropertyInfo(new Godot.Collections.Dictionary
        {
            ["name"] = FoliageLibrary.Setting,
            ["type"] = (int)Variant.Type.String,
            ["hint"] = (int)PropertyHint.File,
            ["hint_string"] = "*.tres,*.res",
        });
    }

    /// <summary>Follows changes to a library and its types (renamed, recategorised, added), letting go of the one before.</summary>
    private void Watch(FoliageLibrary? library)
    {
        var refill = new Callable(this, MethodName.ShowTypes);
        foreach (var resource in Watched(_library))
        {
            if (resource.IsConnected(Resource.SignalName.Changed, refill))
                resource.Disconnect(Resource.SignalName.Changed, refill);
        }

        _library = library;
        foreach (var resource in Watched(_library))
        {
            if (!resource.IsConnected(Resource.SignalName.Changed, refill))
                resource.Connect(Resource.SignalName.Changed, refill);
        }
    }

    private static IEnumerable<Resource> Watched(FoliageLibrary? library) =>
        library is null ? [] : library.Types.OfType<Resource>().Prepend(library);

    public override void _ExitTree()
    {
        var inspector = EditorInterface.Singleton.GetInspector();
        var edited = new Callable(this, MethodName.PropertyEdited);
        if (inspector.IsConnected(EditorInspector.SignalName.PropertyEdited, edited))
            inspector.Disconnect(EditorInspector.SignalName.PropertyEdited, edited);
        if (_toolbar is not null)
        {
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);
            _toolbar.QueueFree();
        }

        Watch(null);
        PluginDocks.Remove(this, _dock);
        if (_inspector is not null)
            RemoveInspectorPlugin(_inspector);
    }

    /// <summary>Shows one of the plugin's icons in place of a button's text.</summary>
    private static void Iconify(Button button, string icon)
    {
        button.Icon = GD.Load<Texture2D>($"res://addons/unpleasant_foliage/Icons/{icon}.svg");
        button.AddThemeConstantOverride("icon_max_width", 20);
        button.TooltipText = $"{button.Text}: {button.TooltipText}";
        button.Text = "";
    }

    private void ToolToggled(bool on)
    {
        _brushAt = null;
        UpdateOverlays();
    }

    public override bool _Handles(GodotObject @object) => @object is FoliageField;

    public override void _Edit(GodotObject? @object)
    {
        _field = @object as FoliageField;
        ShowTypes();
    }

    public override void _MakeVisible(bool visible)
    {
        PluginDocks.Show(this, _dock, visible);
        if (_toolbar is not null)
            _toolbar.Visible = visible;
        if (!visible)
        {
            _brushAt = null;
            UpdateOverlays();
        }
    }

    /// <summary>
    /// The library's types in the dock, a section for each category, each with a thumbnail of its
    /// scene once one is made; those picked before still picked.
    /// </summary>
    private void ShowTypes()
    {
        if (_sections is null || !IsInstanceValid(_sections))
            return;
        var picked = Picked().ToHashSet();
        foreach (var old in _sections.GetChildren())
        {
            _sections.RemoveChild(old);
            old.QueueFree();
        }

        _lists.Clear();
        _shown.Clear();
        var library = _field is not null && IsInstanceValid(_field) ? _field.UsedLibrary : null;
        Watch(library);
        if (_hint is not null)
            _hint.Text = _field is null ? "Select a FoliageField to paint with its types."
                : library is null ? "Give the FoliageField a Library, or set the project's in Project Settings > Unpleasant Foliage > Library."
                : library.Types.Count == 0 ? "Add FoliageTypes to the library's Types."
                : "Pick what to paint or erase (ctrl- or shift-click for several):";
        if (library is null)
            return;

        foreach (var section in library.Types.Where(t => t is not null).GroupBy(t => t.Section).OrderBy(g => g.Key == "Other").ThenBy(g => g.Key))
        {
            _sections.AddChild(new Label { Text = section.Key, ThemeTypeVariation = "HeaderSmall" });
            var list = new ItemList
            {
                SelectMode = ItemList.SelectModeEnum.Multi, MaxColumns = 0, SameColumnWidth = true, IconMode = ItemList.IconModeEnum.Top,
                FixedIconSize = new Vector2I(96, 96), FixedColumnWidth = 110, AutoHeight = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TooltipText = "Ctrl- or shift-click to pick several: painting scatters all of them.",
            };
            _sections.AddChild(list);
            _lists.Add(list);
            foreach (var type in section.OrderBy(FoliageField.Name))
            {
                var index = list.AddItem(FoliageField.Name(type));
                list.SetItemMetadata(index, _shown.Count);
                _shown.Add(type);
                if (picked.Contains(type))
                    list.Select(index, false);
                // A thumbnail of its first mesh: scenes only have one when saved in the editor.
                if (type.Parts().Meshes.FirstOrDefault() is { Mesh: { } mesh })
                    EditorInterface.Singleton.GetResourcePreviewer().QueueEditedResourcePreview(mesh, this, MethodName.Previewed, _shown.Count - 1);
            }
        }
    }

    private void Previewed(string path, Texture2D? preview, Texture2D? thumbnail, int shown)
    {
        if (preview is null)
            return;
        foreach (var list in _lists.Where(IsInstanceValid))
        {
            for (var i = 0; i < list.ItemCount; i++)
            {
                if (list.GetItemMetadata(i).AsInt32() == shown)
                    list.SetItemIcon(i, preview);
            }
        }
    }

    /// <summary>The types picked in the dock, in any section.</summary>
    private IEnumerable<FoliageType> Picked()
    {
        foreach (var list in _lists.Where(IsInstanceValid))
        {
            foreach (var index in list.GetSelectedItems())
            {
                var at = list.GetItemMetadata(index).AsInt32();
                if (at >= 0 && at < _shown.Count)
                    yield return _shown[at];
            }
        }
    }

    private bool Active => _field is not null && (_paintButton?.ButtonPressed == true || _eraseButton?.ButtonPressed == true);

    public override int _Forward3DGuiInput(Camera3D camera, InputEvent @event)
    {
        if (!Active || _field is null)
            return (int)AfterGuiInput.Pass;
        _camera = camera;
        switch (@event)
        {
            case InputEventMouseMotion motion:
                _brushAt = Ground(camera, motion.Position);
                if (_stroking && _brushAt is { } at && at.DistanceTo(_lastDab) > (float)_radius!.Value * 0.3f)
                    Dab(at);
                UpdateOverlays();
                return _stroking ? (int)AfterGuiInput.Stop : (int)AfterGuiInput.Pass;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed)
                {
                    if (Ground(camera, click.Position) is not { } start)
                        return (int)AfterGuiInput.Pass;
                    _erasing = click.CtrlPressed || _eraseButton!.ButtonPressed;
                    // Something to paint or erase picked (erasing all needs nothing picked).
                    if (!Picked().Any() && !(_erasing && _eraseAll!.ButtonPressed))
                    {
                        GD.PushWarning("Foliage: pick something in the Foliage dock first.");
                        return (int)AfterGuiInput.Stop;
                    }

                    _stroking = true;
                    _before = _field.Snapshot();
                    Dab(start);
                    return (int)AfterGuiInput.Stop;
                }

                if (_stroking)
                    EndStroke();
                return (int)AfterGuiInput.Stop;
        }

        return (int)AfterGuiInput.Pass;
    }

    /// <summary>One touch of the brush where it is.</summary>
    private void Dab(Vector3 at)
    {
        if (_field is null)
            return;
        _lastDab = at;
        var radius = (float)_radius!.Value;
        var picked = Picked().ToList();
        if (_erasing)
        {
            _field.Erase(at, radius, _eraseAll!.ButtonPressed ? null : picked);
            return;
        }

        // Painted here for the first time: taken into the field's types (the scene changed with it).
        if (picked.Count(_field.Adopt) > 0)
            EditorInterface.Singleton.MarkSceneAsUnsaved();
        _field.Paint(at, radius, picked, (float)_density!.Value, _random);
    }

    /// <summary>The stroke done: written to the field's data, and one step to undo.</summary>
    private void EndStroke()
    {
        _stroking = false;
        if (_field is null || _before is null)
            return;
        _field.Commit();
        var after = _field.Snapshot();
        var undo = GetUndoRedo();
        undo.CreateAction(_erasing ? "Erase foliage" : "Paint foliage");
        undo.AddDoMethod(_field, FoliageField.MethodName.Restore, after);
        undo.AddUndoMethod(_field, FoliageField.MethodName.Restore, _before);
        undo.CommitAction(false);
        _before = null;
    }

    /// <summary>The brush: a circle on the ground round where it is.</summary>
    public override void _Forward3DDrawOverViewport(Control overlay)
    {
        if (!Active || _brushAt is not { } centre || _camera is null || _field is null)
            return;
        var radius = (float)_radius!.Value;
        var points = new List<Vector2>();
        for (var i = 0; i <= 48; i++)
        {
            var angle = i * MathF.Tau / 48f;
            var at = centre + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            if (_field.GroundAt(at) is { } ground)
                at.Y = ground.Height;
            if (_camera.IsPositionBehind(at))
                return;
            points.Add(_camera.UnprojectPosition(at));
        }

        var colour = _erasing && _stroking || _eraseButton?.ButtonPressed == true ? new Color(1f, 0.4f, 0.3f) : new Color(0.5f, 1f, 0.5f);
        overlay.DrawPolyline(points.ToArray(), colour, 2f, true);
    }

    /// <summary>Where the mouse is on the ground: where the field's ground says it is, or else the first solid thing its ray hits.</summary>
    private Vector3? Ground(Camera3D camera, Vector2 mouse)
    {
        var from = camera.ProjectRayOrigin(mouse);
        var way = camera.ProjectRayNormal(mouse);
        if (_field?.Ground()?.Intersect(from, way) is { } hit)
            return hit;

        var space = camera.GetWorld3D().DirectSpaceState;
        var result = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + way * camera.Far));
        return result.Count > 0 ? result["position"].AsVector3() : null;
    }
}
#endif
