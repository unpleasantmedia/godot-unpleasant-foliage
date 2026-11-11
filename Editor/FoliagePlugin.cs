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
/// The dock also picks which of the ground's textures foliage may go on (kept with the project).
/// Autopaint fills the field's outline with what is picked, where there is room; with a
/// <see cref="FoliageContainer"/> selected, every field in it (one step to undo either way).
/// </summary>
[Tool]
public partial class FoliagePlugin : EditorPlugin
{
    private HBoxContainer? _toolbar;
    private Button? _paintButton;
    private Button? _eraseButton;
    private SpinBox? _radius;
    private SpinBox? _density;
    private SpinBox? _falloff;
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
    private FoliageContainer? _container;
    private Button? _autopaint;
    private readonly List<Control> _brushTools = [];

    /// <summary>The ground's textures, to pick those foliage may go on.</summary>
    private ItemList? _grounds;
    private Label? _groundsHint;

    /// <summary>Where the ground textures picked are kept, in the project's editor metadata.</summary>
    private const string MetadataSection = "unpleasant_foliage";
    private const string AllowedKey = "allowed_textures";
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
        var radiusLabel = new Label { Text = "Radius" };
        _toolbar.AddChild(radiusLabel);
        _radius = new SpinBox { MinValue = 0.5, MaxValue = 100, Step = 0.5, Value = 5, Suffix = "m", TooltipText = "How big the brush is." };
        _toolbar.AddChild(_radius);
        _brushTools.AddRange([_paintButton, _eraseButton, radiusLabel, _radius]);
        _toolbar.AddChild(new Label { Text = "Density" });
        _density = new SpinBox { MinValue = 0.05, MaxValue = 4, Step = 0.05, Value = 1, Suffix = "×", TooltipText = "How thickly to paint, as a share of each type's own density." };
        _toolbar.AddChild(_density);
        var falloffLabel = new Label { Text = "Edge Falloff" };
        _toolbar.AddChild(falloffLabel);
        _falloff = new SpinBox { MinValue = 0, MaxValue = 500, Step = 1, Value = 0, Suffix = "m", TooltipText =
            "Autopaint: how far in from a field's outline the foliage thins out, from none at the edge to its full density this far in. 0: full density right up to the edge." };
        _toolbar.AddChild(_falloff);
        _eraseAll = new CheckBox { Text = "All", TooltipText = "Erase every type, not only those picked in the Foliage dock." };
        _toolbar.AddChild(_eraseAll);
        _brushTools.Add(_eraseAll);
        _autopaint = new Button { Text = "Autopaint", Flat = true, TooltipText =
            "Fill the field's outline (or, with a FoliageContainer selected, every field in it) with the foliage picked in the Foliage dock, " +
            "at the density set, on the ground textures picked there: only where there is room, what is there kept. One step to undo." };
        _autopaint.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.Autopaint));
        _toolbar.AddChild(_autopaint);
        _toolbar.Visible = false;
        ToolButtons.StyleAll(_toolbar);
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _toolbar);

        AddLibrarySetting();
        _dock = new VBoxContainer { Name = "Foliage" };
        _hint = new Label { Text = "Select a FoliageField to paint with its types.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(new Label { Text = "Paint on", ThemeTypeVariation = "HeaderSmall" });
        _groundsHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(_groundsHint);
        _grounds = new ItemList { SelectMode = ItemList.SelectModeEnum.Multi, AutoHeight = true, MaxColumns = 0, SameColumnWidth = true, FixedColumnWidth = 110,
            TooltipText = "Ctrl- or shift-click for several. Foliage goes only where nothing but the ground textures picked shows; none picked: anywhere." };
        _grounds.Connect(ItemList.SignalName.MultiSelected, new Callable(this, MethodName.GroundsPicked));
        _dock.AddChild(_grounds);
        _roadBuffer = new CheckBox { Text = "Keep Road Buffers", ButtonPressed = Saved("road_buffer", true).AsBool(),
            TooltipText = "Foliage is kept off roads' foliage buffers (set on the road network, or a road of its own): hidden there, removed on save, and not painted there." };
        _roadBuffer.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.RoadBufferToggled));
        _dock.AddChild(_roadBuffer);
        FoliageField.SetBufferKept(FoliageField.RoadBuffers, _roadBuffer.ButtonPressed);
        _buildingBuffer = new CheckBox { Text = "Keep Building Buffers", ButtonPressed = Saved("building_buffer", true).AsBool(),
            TooltipText = "Foliage is kept off buildings' foliage buffers (set on the building cluster, or a building of its own): hidden there, removed on save, and not painted there." };
        _buildingBuffer.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.BuildingBufferToggled));
        _dock.AddChild(_buildingBuffer);
        FoliageField.SetBufferKept(FoliageField.BuildingBuffers, _buildingBuffer.ButtonPressed);
        _dock.AddChild(new HSeparator());
        AddNoiseSection(_dock);
        _dock.AddChild(new HSeparator());
        _dock.AddChild(_hint);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _sections = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(_sections);
        _dock.AddChild(scroll);
        ProjectSettings.Singleton.Connect(ProjectSettings.SignalName.SettingsChanged, new Callable(this, MethodName.ShowTypes));
        // Below the Inspector, so both show while the field is being edited.
        // In its slot only while the plugin is shown: see _MakeVisible.
        EditorInterface.Singleton.GetInspector().Connect(EditorInspector.SignalName.PropertyEdited, new Callable(this, MethodName.PropertyEdited));
        EditorInterface.Singleton.GetSelection().Connect(EditorSelection.SignalName.SelectionChanged, new Callable(this, MethodName.SelectionChanged));
    }

    /// <summary>
    /// The level saved: what masks hide in its fields (painted where a road, a building, a lot or a
    /// square has since gone) taken out of their data for good.
    /// </summary>
    public override void _SaveExternalData()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root || !IsInsideTree())
            return;
        foreach (var field in FoliageField.InTree(GetTree()).Where(f => f == root || root.IsAncestorOf(f)))
        {
            if (field.PruneMasked() is > 0 and var gone)
                GD.Print($"Foliage: took {gone} out of {((Node)field).Name}, under roads, buildings, lots or squares.");
        }
    }

    /// <summary>The selection changed: each field's outline shown or hidden for it.</summary>
    private void SelectionChanged()
    {
        if (!IsInsideTree())
            return;
        foreach (var field in FoliageField.InTree(GetTree()))
            field.ShowOutline();
    }

    /// <summary>The field whose library and ground the dock shows: the one selected, or the first in the container selected.</summary>
    private FoliageField? Shown => _field is not null && IsInstanceValid(_field) ? _field
        : _container is not null && IsInstanceValid(_container) ? _container.Fields.FirstOrDefault() : null;

    private CheckBox? _roadBuffer;

    private CheckBox? _buildingBuffer;

    private void RoadBufferToggled(bool on)
    {
        Save("road_buffer", on);
        FoliageField.SetBufferKept(FoliageField.RoadBuffers, on);
    }

    private void BuildingBufferToggled(bool on)
    {
        Save("building_buffer", on);
        FoliageField.SetBufferKept(FoliageField.BuildingBuffers, on);
    }

    /// <summary>The kinds of buffer painting keeps off (see FoliageField.KeptBuffers).</summary>
    private static IReadOnlyCollection<string> KeepBuffers => FoliageField.KeptBuffers;

    // ---- Density noise ----------------------------------------------------------------------

    /// <summary>The noises to pick from (None first), and its settings: kept with the project.</summary>
    private ItemList? _noises;
    private SpinBox? _noiseSize, _noiseOctaves, _noiseSeed, _noiseOpen;
    private Control? _noiseSettings;

    private static Variant Saved(string key, Variant fallback) =>
        EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(MetadataSection, key, fallback);

    private static void Save(string key, Variant value) =>
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(MetadataSection, key, value);

    /// <summary>
    /// The dock's Density Noise section: a noise to thin foliage out by (or None), with a picture of
    /// each, and its settings: how big its patches are, how many octaves, its seed, and how much is open.
    /// </summary>
    private void AddNoiseSection(VBoxContainer dock)
    {
        dock.AddChild(new Label { Text = "Density Noise", ThemeTypeVariation = "HeaderSmall" });
        dock.AddChild(new Label { Text = "Thins foliage out by a noise over the world, open where it is lowest; spacing still holds.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _noises = new ItemList { SelectMode = ItemList.SelectModeEnum.Single, AutoHeight = true, MaxColumns = 0, SameColumnWidth = true, IconMode = ItemList.IconModeEnum.Top,
            FixedIconSize = new Vector2I(48, 48), FixedColumnWidth = 64, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _noises.AddItem("None");
        foreach (var (name, type) in FoliageDensityNoise.Noises)
            _noises.AddItem(name, FoliageDensityNoise.Preview(type));
        _noises.Select(Math.Clamp(Saved("noise", 0).AsInt32(), 0, _noises.ItemCount - 1));
        _noises.Connect(ItemList.SignalName.ItemSelected, new Callable(this, MethodName.NoisePicked));
        dock.AddChild(_noises);

        var grid = new GridContainer { Columns = 2 };
        SpinBox Setting(string label, string key, double min, double max, double step, double fallback, string suffix, string tip)
        {
            grid.AddChild(new Label { Text = label, TooltipText = tip });
            var box = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = Saved(key, fallback).AsDouble(), Suffix = suffix, TooltipText = tip,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AllowGreater = true };
            box.SetMeta("key", key);
            box.Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, MethodName.NoiseSettingChanged));
            grid.AddChild(box);
            return box;
        }

        _noiseSize = Setting("Size", "noise_size", 10, 2000, 5, 150, "m", "How far across its patches (and clearings) are.");
        _noiseOctaves = Setting("Octaves", "noise_octaves", 1, 8, 1, 3, "", "How much finer detail is laid over its patches.");
        _noiseSeed = Setting("Seed", "noise_seed", 0, 99999, 1, 0, "", "Which of its patterns: another seed puts the patches elsewhere.");
        _noiseOpen = Setting("Openness", "noise_open", 0, 95, 1, 35, "%", "How much of the land is left open: clearings where the noise is lowest.");
        _noiseSettings = grid;
        dock.AddChild(grid);
        _noiseSettings.Visible = _noises.IsSelected(0) == false;
    }

    private void NoisePicked(long index)
    {
        Save("noise", (int)index);
        if (_noiseSettings is not null)
            _noiseSettings.Visible = index != 0;
    }

    private void NoiseSettingChanged(double value)
    {
        foreach (var box in new[] { _noiseSize, _noiseOctaves, _noiseSeed, _noiseOpen })
        {
            if (box is not null)
                Save(box.GetMeta("key").AsString(), box.Value);
        }
    }

    /// <summary>The density noise picked in the dock, or null for None.</summary>
    private FoliageDensityNoise? PickedNoise()
    {
        var picked = _noises?.GetSelectedItems() is { Length: > 0 } items ? items[0] : 0;
        if (picked <= 0 || picked > FoliageDensityNoise.Noises.Length)
            return null;
        return new FoliageDensityNoise(FoliageDensityNoise.Noises[picked - 1].Type, (float)(_noiseSize?.Value ?? 150), (int)(_noiseOctaves?.Value ?? 3),
            (int)(_noiseSeed?.Value ?? 0), (float)(_noiseOpen?.Value ?? 35) / 100f);
    }

    /// <summary>The ground textures picked, as kept with the project.</summary>
    private static string[] Allowed() =>
        EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(MetadataSection, AllowedKey, System.Array.Empty<string>()).AsStringArray();

    private void GroundsPicked(long index, bool selected)
    {
        if (_grounds is null)
            return;
        var picked = _grounds.GetSelectedItems().Select(i => _grounds.GetItemText(i)).ToArray();
        EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(MetadataSection, AllowedKey, picked);
    }

    /// <summary>The ground's textures listed, those picked before still picked.</summary>
    private void ShowGrounds()
    {
        if (_grounds is null || !IsInstanceValid(_grounds))
            return;
        _grounds.Clear();
        var names = Shown?.Ground()?.TextureNames ?? [];
        var allowed = Allowed();
        foreach (var name in names)
        {
            var index = _grounds.AddItem(name);
            if (allowed.Contains(name))
                _grounds.Select(index, false);
        }

        if (_groundsHint is not null)
            _groundsHint.Text = names.Count == 0 ? "This ground has no textures to pick from: foliage goes anywhere."
                : "Foliage goes only where the textures picked show (none picked: anywhere):";
    }

    /// <summary>
    /// Fills the field selected (or every field in the container selected) with the types picked, at
    /// the density set, on the textures picked: one step to undo.
    /// </summary>
    private void Autopaint()
    {
        if (_running.Count > 0)
        {
            GD.PushWarning("Foliage: autopainting already; wait for it to finish.");
            return;
        }

        var fields = _field is not null && IsInstanceValid(_field) ? [_field]
            : _container is not null && IsInstanceValid(_container) ? _container.Fields.ToList() : new List<FoliageField>();
        var picked = Picked().ToList();
        if (fields.Count == 0 || picked.Count == 0)
        {
            GD.PushWarning(fields.Count == 0 ? "Foliage: the container has no fields to fill." : "Foliage: pick something in the Foliage dock first.");
            return;
        }

        // Made ready here (all they need of the scene taken now), worked out on threads of their own,
        // one a field (the editor going on meanwhile), and put in back here as they all finish.
        var allowed = Allowed();
        _undoName = _container is not null ? $"Autopaint {_container.Name}" : $"Autopaint {((Node)fields[0]).Name}";
        foreach (var field in fields)
        {
            var before = field.Snapshot();
            if (field.PrepareFill(picked, (float)_density!.Value, (ulong)_random.NextInt64(), allowed, (float)_falloff!.Value, PickedNoise(), KeepBuffers) is not { } job)
                continue;
            _running.Add((job, System.Threading.Tasks.Task.Run(() => job.Run()), before));
        }

        _started = Time.GetTicksMsec();
        _shownAt = Time.GetTicksMsec();
        // While it reads the terrain, the terrain is not to be edited (the terrain plugin looks at this).
        if (_running.Count > 0)
            FoliageJob.TerrainReaders(+1);
        if (_autopaint is not null)
            _autopaint.Disabled = true;
        GD.Print($"Foliage: autopainting {_running.Count} field{(_running.Count == 1 ? "" : "s")}...");
    }

    /// <summary>The autopainting under way: each field's job, its thread, and what the field had before (to undo).</summary>
    private readonly List<(FoliageField.FillJob Job, System.Threading.Tasks.Task Task, Godot.Collections.Dictionary<string, float[]> Before)> _running = [];
    private string _undoName = "";
    private ulong _started;
    private ulong _shownAt;

    /// <summary>How the autopainting goes: its progress in the Output every 5 seconds, and put in when every field is done.</summary>
    public override void _Process(double delta)
    {
        if (_running.Count == 0)
            return;
        var total = _running.Sum(r => r.Job.Total);
        var percent = total == 0 ? 100 : (int)(_running.Sum(r => Math.Min(r.Job.Done, r.Job.Total)) * 100 / total);
        if (Time.GetTicksMsec() - _shownAt >= 5000)
        {
            _shownAt = Time.GetTicksMsec();
            GD.Print($"Foliage: autopainting... {percent}%");
        }

        if (_running.Any(r => !r.Task.IsCompleted))
            return;

        var undo = GetUndoRedo();
        undo.CreateAction(_undoName);
        var added = 0;
        foreach (var (job, task, before) in _running)
        {
            var field = job.Field;
            if (task.IsFaulted)
            {
                GD.PushError($"Foliage: autopainting {(IsInstanceValid(field) ? ((Node)field).Name.ToString() : "a field")} failed: {task.Exception?.GetBaseException().Message}");
                continue;
            }

            if (!IsInstanceValid(field) || !field.IsInsideTree())
                continue;
            added += field.ApplyFill(job);
            field.Commit();
            undo.AddDoMethod(field, FoliageField.MethodName.Restore, field.Snapshot());
            undo.AddUndoMethod(field, FoliageField.MethodName.Restore, before);
        }

        undo.CommitAction(false);
        EditorInterface.Singleton.MarkSceneAsUnsaved();
        GD.Print($"Foliage: autopainted {added} in {_running.Count} field{(_running.Count == 1 ? "" : "s")} ({(Time.GetTicksMsec() - _started) / 1000.0:0.0} s).");
        _running.Clear();
        FoliageJob.TerrainReaders(-1);
        if (_autopaint is not null)
            _autopaint.Disabled = false;
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
        var selection = new Callable(this, MethodName.SelectionChanged);
        if (EditorInterface.Singleton.GetSelection().IsConnected(EditorSelection.SignalName.SelectionChanged, selection))
            EditorInterface.Singleton.GetSelection().Disconnect(EditorSelection.SignalName.SelectionChanged, selection);
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

    public override bool _Handles(GodotObject @object) => @object is FoliageField or FoliageContainer;

    public override void _Edit(GodotObject? @object)
    {
        _field = @object as FoliageField;
        _container = @object as FoliageContainer;
        foreach (var field in _container?.Fields ?? (_field is not null ? [_field] : []))
            field.EnsureOutline();
        // The brush is for a field; a container has Autopaint alone.
        foreach (var tool in _brushTools)
            tool.Visible = _container is null;
        if (_container is not null)
        {
            _paintButton?.SetPressedNoSignal(false);
            _eraseButton?.SetPressedNoSignal(false);
        }

        ShowTypes();
        ShowGrounds();
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
        var library = Shown?.UsedLibrary ?? (_container is not null ? FoliageLibrary.ProjectLibrary() : null);
        Watch(library);
        if (_hint is not null)
            _hint.Text = _field is null && _container is null ? "Select a FoliageField to paint with its types."
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
        _field.Paint(at, radius, picked, (float)_density!.Value, _random, Allowed(), PickedNoise(), KeepBuffers);
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
