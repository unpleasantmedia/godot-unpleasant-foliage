using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// Foliage painted over a level's ground (with the Foliage plugin's brush) from its
/// <see cref="Library"/>: for each type painted, the places painted, kept in <see cref="Data"/>
/// (a file of its own, one to a field: a field draws every type of its library that its data has
/// places for). Each
/// type's meshes are drawn at its places, many to a draw (a MultiMesh per mesh, per stretch of
/// ground <see cref="ChunkSize"/> across, so those out of sight are left out), and its collision
/// shapes are put there too, in a static body per stretch.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_foliage/Icons/Foliage.svg")]
public partial class FoliageField : Node3D
{
    /// <summary>
    /// The library it is painted from (see <see cref="FoliageLibrary"/>); if not set, the project's
    /// (Project Settings &gt; Unpleasant Foliage &gt; Library).
    /// </summary>
    [Export]
    public FoliageLibrary? Library
    {
        get => _library;
        set
        {
            _library = value;
            QueueRebuild();
        }
    }

    private FoliageLibrary? _library;

    /// <summary>The library it is painted from: its own, or the project's.</summary>
    public FoliageLibrary? UsedLibrary => Library ?? FoliageLibrary.ProjectLibrary();

    /// <summary>
    /// The kinds of foliage it has: those of its library its data has places for, found as it is
    /// built; and those painted here since (see <see cref="Adopt"/>).
    /// </summary>
    private Godot.Collections.Array<FoliageType> _types = [];

    /// <summary>
    /// A type painted here for the first time taken into the kinds it has, without building it all
    /// again (painting builds what it puts down itself). False if it had it already.
    /// </summary>
    public bool Adopt(FoliageType type)
    {
        if (_types.Contains(type))
            return false;
        _types.Add(type);
        return true;
    }

    /// <summary>Where its foliage is: made beside the level the first time something is painted.</summary>
    [Export]
    public FoliageData? Data
    {
        get => _data;
        set
        {
            _data = value;
            QueueRebuild();
        }
    }

    private FoliageData? _data;

    /// <summary>The node that is the ground its foliage stands on; if not set, the project's <see cref="FoliageGround.Finder"/> picks one.</summary>
    [Export]
    public Node3D? Terrain { get; set; }

    /// <summary>How far across each stretch of ground its foliage is drawn and made solid in, in metres.</summary>
    public const float ChunkSize = 32f;

    /// <summary>
    /// The same for trees: bigger, as a forest has few trees to a stretch, and each stretch of each
    /// tree is a draw of its own (one for each of its meshes, again for each of the sun's shadow
    /// cascades): in 32-metre stretches a forest in view took thousands of draws.
    /// </summary>
    public const float TreeChunkSize = 128f;

    /// <summary>Each type's places, by its file, as built.</summary>
    private readonly Dictionary<string, List<Transform3D>> _placed = [];

    /// <summary>What each stretch of each type is built as: its draws and its body.</summary>
    private readonly Dictionary<(string Type, int X, int Z), (List<Draw> Draws, StaticBody3D? Body)> _chunks = [];

    /// <summary>
    /// One mesh of a stretch's foliage drawn many times: made straight on the rendering server, not
    /// as a node (a big forest is tens of thousands of them: as nodes, they held up loading a level
    /// for many seconds). Whether it is a tree's, for its view distance and its shadows.
    /// </summary>
    private sealed record Draw(Rid Instance, MultiMesh Multi, bool Tall)
    {
        /// <summary>Whether it casts shadows now (see _Process): only told the rendering server when it changes.</summary>
        public bool Shadows { get; set; } = true;
    }

    /// <summary>How far off a draw is drawn: in a game, no further than its player set (trees to the tree
    /// distance, the rest to the foliage distance); in the editor, its own (or hundreds of thousands are).</summary>
    private static void DrawDistance(Draw draw)
    {
        var distance = Engine.IsEditorHint() ? draw.Tall ? EditorTreeDistance : EditorViewDistance
            : draw.Tall ? TreeDistance : ViewDistance;
        RenderingServer.InstanceGeometrySetVisibilityRange(draw.Instance, 0f, Mathf.Max(distance, 0f), 0f, 0f, RenderingServer.VisibilityRangeFadeMode.Disabled);
    }

    /// <summary>The view distances set (see ViewDistance and TreeDistance): every field's foliage drawn to them.</summary>
    public static void ApplyDistances(SceneTree tree)
    {
        foreach (var field in InTree(tree))
        {
            foreach (var (draws, _) in field._chunks.Values)
            {
                foreach (var draw in draws)
                    DrawDistance(draw);
            }
        }
    }

    /// <summary>The group of its draws, for a game's view distance setting to find.</summary>
    public const string ViewDistanceGroup = "FoliageViewDistance";

    /// <summary>In a game, how far off foliage is drawn, in metres, as its player set it; 0 for as far as can be seen.</summary>
    public static float ViewDistance { get; set; }

    /// <summary>The group of trees' draws, drawn to the tree distance.</summary>
    public const string TreeGroup = "TreeViewDistance";

    /// <summary>In a game, how far off trees are drawn, as its player set it; 0 for as far as can be seen.</summary>
    public static float TreeDistance { get; set; }

    /// <summary>How tall a piece of foliage is, at least, to be a tree, in metres.</summary>
    private const float TallHeight = 2f;

    private readonly Dictionary<string, (List<(Mesh Mesh, Material? Material, Transform3D At)> Meshes, List<(Shape3D Shape, Transform3D At)> Shapes)> _parts = [];
    private bool _rebuildQueued;

    /// <summary>Each type, by its key: for how far it sinks and how far apart it stands.</summary>
    private readonly Dictionary<string, FoliageType> _typesByKey = [];

    // Built each time it comes into the tree, not only the first: the editor takes a scene out of the
    // tree when another scene's tab is opened, and puts it back (without readying it again) on return.
    public override void _EnterTree()
    {
        AddToGroup(Group);
        QueueRebuild();
        OutlineOnEnter();
        Entered?.Invoke(this);
    }

    /// <summary>A field come into the tree: for whatever sets masks on fields to set its own on this one too.</summary>
    public static event Action<FoliageField>? Entered;

    public override void _ExitTree() => Clear();

    public override void _Ready() => SetNotifyTransform(true);

    // Its draws are its own on the rendering server: shown or hidden, and moved, with it.
    public override void _Notification(int what)
    {
        if (what is not ((int)NotificationVisibilityChanged or (int)NotificationTransformChanged) || !IsInsideTree())
            return;
        var (visible, at) = (IsVisibleInTree(), GlobalTransform);
        foreach (var (draws, _) in _chunks.Values)
        {
            foreach (var draw in draws)
            {
                RenderingServer.InstanceSetVisible(draw.Instance, visible);
                RenderingServer.InstanceSetTransform(draw.Instance, at);
            }
        }
    }

    /// <summary>
    /// How near the camera trees cast shadows, in metres, in a game: further off, theirs are too small
    /// to make out, while each stretch of trees casting them is a draw again for every cascade of the sun's.
    /// </summary>
    public const float TreeShadowDistance = 150f;

    /// <summary>How far off foliage is drawn in the editor, in metres: trees, and the rest.</summary>
    public const float EditorTreeDistance = 800f;
    public const float EditorViewDistance = 150f;

    /// <summary>The camera of the editor's first 3D view, or null outside the editor.</summary>
    private static Camera3D? EditorCamera()
    {
#if TOOLS
        return EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();
#else
        return null;
#endif
    }

    /// <summary>How often the trees' shadows are looked over as the camera moves, in seconds.</summary>
    private const double ShadowCheckEvery = 0.25;

    private double _shadowCheck;

    public override void _Process(double delta)
    {
        if (_tall.Count == 0 || (_shadowCheck -= delta) > 0)
            return;
        _shadowCheck = ShadowCheckEvery;
        // In the editor, as its 3D view's camera sees it.
        var camera = Engine.IsEditorHint() ? EditorCamera() : GetViewport()?.GetCamera3D();
        if (camera is null)
            return;
        var at = camera.GlobalPosition;
        foreach (var ((key, x, z), (draws, _)) in _chunks)
        {
            if (!_tall.Contains(key))
                continue;
            // How far off the nearest part of its stretch is, across the ground.
            var nearX = Mathf.Clamp(at.X, x * TreeChunkSize, (x + 1) * TreeChunkSize);
            var nearZ = Mathf.Clamp(at.Z, z * TreeChunkSize, (z + 1) * TreeChunkSize);
            var near = new Vector2(at.X - nearX, at.Z - nearZ).Length() <= TreeShadowDistance;
            var cast = near ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
            foreach (var draw in draws)
            {
                if (draw.Shadows == near)
                    continue;
                draw.Shadows = near;
                RenderingServer.InstanceGeometrySetCastShadowsSetting(draw.Instance, (RenderingServer.ShadowCastingSetting)cast);
            }
        }
    }

    /// <summary>The group every field in the tree is in.</summary>
    public const string Group = "foliage_fields";

    /// <summary>Every field in the tree: for another plugin to set its masks on.</summary>
    public static IEnumerable<FoliageField> InTree(SceneTree tree) => tree.GetNodesInGroup(Group).OfType<FoliageField>();

    /// <summary>
    /// A type's key, for its places in the data: its file, when it has one of its own; or else
    /// (one made inside the level) its scene's file.
    /// </summary>
    public static string Key(FoliageType type) =>
        !string.IsNullOrEmpty(type.ResourcePath) && !type.ResourcePath.Contains("::") ? type.ResourcePath
        : type.Scene?.ResourcePath is { Length: > 0 } scene ? scene : type.ResourceName;

    /// <summary>What a type is called: its name, or else its file's or its scene's.</summary>
    public static new string Name(FoliageType type) =>
        !string.IsNullOrEmpty(type.ResourceName) ? type.ResourceName
        : !string.IsNullOrEmpty(type.ResourcePath) && !type.ResourcePath.Contains("::") ? type.ResourcePath.GetFile().GetBaseName()
        : type.Scene?.ResourcePath.GetFile().GetBaseName() ?? "Foliage";

    /// <summary>Builds it all again at the end of the frame, from its data.</summary>
    public void QueueRebuild()
    {
        if (_rebuildQueued || !IsInsideTree())
            return;
        _rebuildQueued = true;
        Callable.From(Rebuild).CallDeferred();
    }

    private void Rebuild()
    {
        _rebuildQueued = false;
        FollowGround();
        _crowns.Clear();
        Clear();
        _placed.Clear();
        _parts.Clear();
        if (_data is null)
            return;
        // Every type of its library its data has places for (what it has of types no longer in the
        // library is kept in the data, but not drawn).
        _types = [.. UsedLibrary?.Types.Where(t => t is not null && _data.Placed.ContainsKey(Key(t))) ?? []];
        foreach (var type in _types.Where(t => t is not null))
        {
            var key = Key(type);
            _parts[key] = type.Parts();
            if (_parts[key].Meshes.Any(m => m.Mesh.GetAabb().Size.Y > TallHeight))
                _tall.Add(key);
            else
                _tall.Remove(key);
            _typesByKey[key] = type;
            _placed[key] = _data.Placed.TryGetValue(key, out var numbers) ? Unpack(numbers) : [];
            // On the ground as it is now, wherever it was when painted.
            var places = _placed[key];
            for (var i = 0; i < places.Count; i++)
                places[i] = OnGround(places[i], type);
            // Each stretch with its places, sorted out in one go (not every place gone over for each stretch:
            // with hundreds of thousands of trees, that held up loading a level for minutes).
            foreach (var stretch in places.GroupBy(t => Chunk(key, t)))
                BuildChunk(key, stretch.Key.X, stretch.Key.Z, [.. stretch]);
        }

    }

    /// <summary>Takes away everything it has built.</summary>
    private void Clear()
    {
        foreach (var (draws, _) in _chunks.Values)
        {
            foreach (var draw in draws)
                RenderingServer.FreeRid(draw.Instance);
        }

        // All it built, including any left from before its scripts were reloaded (it forgets them then).
        foreach (var built in GetChildren(true).Where(c => c is MultiMeshInstance3D or StaticBody3D && c.Name.ToString().StartsWith(BuiltName)))
        {
            RemoveChild(built);
            built.QueueFree();
        }

        _chunks.Clear();
        foreach (var body in _bodies.Values)
            PhysicsServer3D.FreeRid(body);
        _bodies.Clear();
        _bodiesChanged.Clear();
    }

    /// <summary>How many nodes it has built: each named with its count, so no two are alike.</summary>
    private int _built;

    /// <summary>What the nodes it builds are called, to tell them from anything else under it.</summary>
    private const string BuiltName = "Foliage";

    /// <summary>Which stretch of ground a place of a type is in: trees' stretches are bigger (see <see cref="TreeChunkSize"/>).</summary>
    private (int X, int Z) Chunk(string key, Transform3D at)
    {
        var size = _tall.Contains(key) ? TreeChunkSize : ChunkSize;
        return ((int)MathF.Floor(at.Origin.X / size), (int)MathF.Floor(at.Origin.Z / size));
    }

    /// <summary>The types that are trees (a part taller than <see cref="TallHeight"/>).</summary>
    private readonly HashSet<string> _tall = [];

    /// <summary>
    /// Builds one stretch of one type again, from its places there: in the nodes it already has, if it
    /// has them, as adding and taking away nodes makes the editor go over the whole level again.
    /// </summary>
    /// <summary>Builds these stretches again, each type's places sorted out by stretch in one go.</summary>
    private void BuildChunks(IEnumerable<(string Key, int X, int Z)> stretches)
    {
        foreach (var byType in stretches.GroupBy(t => t.Key))
        {
            if (!_placed.TryGetValue(byType.Key, out var all))
                continue;
            var wanted = byType.Select(t => (t.X, t.Z)).ToHashSet();
            var sorted = wanted.ToDictionary(w => w, _ => new List<Transform3D>());
            foreach (var place in all)
            {
                if (sorted.TryGetValue(Chunk(byType.Key, place), out var list))
                    list.Add(place);
            }

            foreach (var ((x, z), here) in sorted)
                BuildChunk(byType.Key, x, z, here);
        }
    }

    private void BuildChunk(string key, int x, int z, List<Transform3D>? those = null)
    {
        // Out of the tree: nothing to draw into (built again in full as it comes back).
        if (!IsInsideTree() || !_placed.TryGetValue(key, out var all) || !_parts.TryGetValue(key, out var parts))
            return;
        // Its places: as given (already sorted out by stretch), or found among all of them.
        var here = (those ?? all.Where(t => Chunk(key, t) == (x, z))).Where(t => !Masked(t.Origin, Height(key, t))).ToList();
        var built = _chunks.TryGetValue((key, x, z), out var old);
        if (here.Count == 0 && !built)
            return;

        // Placed as it is (its own place in the world aside): its foliage is where it was painted.
        var toLocal = GlobalTransform.AffineInverse();
        var draws = built ? old.Draws : [];
        // Each the colour of the ground under it, where its material takes its colour from it (a
        // standard one using its vertex colour, or one marked ground_colour, as
        // a grass material is): the colour the ground gives there.
        var coloured = parts.Meshes.Any(m => m.Material is BaseMaterial3D { VertexColorUseAsAlbedo: true }
            || m.Material?.GetMeta("ground_colour", false).AsBool() == true);
        var colours = coloured ? here.Select(p => GroundColourAt(p.Origin)).ToList() : null;
        for (var d = 0; d < parts.Meshes.Count; d++)
        {
            var (mesh, material, at) = parts.Meshes[d];
            var multi = built ? draws[d].Multi : new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = coloured, Mesh = mesh };
            multi.InstanceCount = here.Count;
            // All its places given at once (one call, not one per place: thousands to a stretch of forest).
            var stride = colours is not null ? 16 : 12;
            var buffer = new float[here.Count * stride];
            for (var i = 0; i < here.Count; i++)
            {
                var t = toLocal * here[i] * at;
                var (b, o, k) = (t.Basis, t.Origin, i * stride);
                (buffer[k], buffer[k + 1], buffer[k + 2], buffer[k + 3]) = (b.X.X, b.Y.X, b.Z.X, o.X);
                (buffer[k + 4], buffer[k + 5], buffer[k + 6], buffer[k + 7]) = (b.X.Y, b.Y.Y, b.Z.Y, o.Y);
                (buffer[k + 8], buffer[k + 9], buffer[k + 10], buffer[k + 11]) = (b.X.Z, b.Y.Z, b.Z.Z, o.Z);
                if (colours is not null)
                    (buffer[k + 12], buffer[k + 13], buffer[k + 14], buffer[k + 15]) = (colours[i].R, colours[i].G, colours[i].B, colours[i].A);
            }

            if (here.Count > 0)
                multi.Buffer = buffer;

            if (built)
                continue;
            var instance = RenderingServer.InstanceCreate2(multi.GetRid(), GetWorld3D().Scenario);
            RenderingServer.InstanceSetTransform(instance, GlobalTransform);
            if (material is not null)
                RenderingServer.InstanceGeometrySetMaterialOverride(instance, material.GetRid());
            RenderingServer.InstanceSetVisible(instance, IsVisibleInTree());
            var draw = new Draw(instance, multi, mesh.GetAabb().Size.Y > TallHeight);
            DrawDistance(draw);
            draws.Add(draw);
        }

        // Solid as its scene is: in the body of its square (see BodySize), built again at the end of the frame.
        if (parts.Shapes.Count > 0)
            BodyChanged(key, x, z);
        _chunks[(key, x, z)] = (draws, null);
    }

    private static List<Transform3D> Unpack(float[] numbers)
    {
        var places = new List<Transform3D>(numbers.Length / 12);
        for (var i = 0; i + 11 < numbers.Length; i += 12)
        {
            var basis = new Basis(new Vector3(numbers[i], numbers[i + 1], numbers[i + 2]), new Vector3(numbers[i + 3], numbers[i + 4], numbers[i + 5]),
                new Vector3(numbers[i + 6], numbers[i + 7], numbers[i + 8]));
            places.Add(new Transform3D(basis, new Vector3(numbers[i + 9], numbers[i + 10], numbers[i + 11])));
        }

        return places;
    }

    private static float[] Pack(List<Transform3D> places)
    {
        var numbers = new float[places.Count * 12];
        for (var i = 0; i < places.Count; i++)
        {
            var (b, o) = (places[i].Basis, places[i].Origin);
            new[] { b.Column0.X, b.Column0.Y, b.Column0.Z, b.Column1.X, b.Column1.Y, b.Column1.Z, b.Column2.X, b.Column2.Y, b.Column2.Z, o.X, o.Y, o.Z }
                .CopyTo(numbers, i * 12);
        }

        return numbers;
    }

    // ---- Masks ------------------------------------------------------------------------------

    /// <summary>
    /// The shapes its foliage is hidden under, by the name each caller gave (see <see cref="FoliageMaskShape"/>):
    /// not saved, so a caller sets them each time it loads. What they hide is drawn and solid no more,
    /// but stays in its data (see <see cref="PruneMasked"/>).
    /// </summary>
    private readonly FoliageMaskIndex _maskIndex = new();

    /// <summary>The areas masks changed in, to build again at the end of the frame.</summary>
    private readonly List<Rect2> _maskChanges = [];

    /// <summary>Hides its foliage inside an outline, in world X and Z, at any height. Setting a name again moves that mask.</summary>
    public void SetMask(string name, Vector2[] outline) => SetMasks(name, [new FoliageMaskShape(outline)]);

    /// <summary>Hides its foliage under these shapes, until the name's masks are set again or removed.</summary>
    public void SetMasks(string name, IReadOnlyList<FoliageMaskShape> shapes)
    {
        foreach (var area in _maskIndex.Set(name, shapes))
            MaskChanged(area);
    }

    /// <summary>A mask over a rectangle, in world X and Z.</summary>
    public void SetMaskRect(string name, Rect2 area) =>
        SetMask(name, [area.Position, area.Position + new Vector2(area.Size.X, 0f), area.End, area.Position + new Vector2(0f, area.Size.Y)]);

    /// <summary>A mask over a circle, in world X and Z.</summary>
    public void SetMaskCircle(string name, Vector2 centre, float radius)
    {
        var outline = new Vector2[48];
        for (var i = 0; i < outline.Length; i++)
            outline[i] = centre + Vector2.Right.Rotated(MathF.Tau * i / outline.Length) * radius;
        SetMask(name, outline);
    }

    /// <summary>Takes a mask away: what it hid shows again.</summary>
    public void RemoveMask(string name)
    {
        foreach (var area in _maskIndex.Remove(name))
            MaskChanged(area);
    }

    private void MaskChanged(Rect2 area)
    {
        if (_maskChanges.Count == 0 && IsInsideTree())
            Callable.From(ApplyMasks).CallDeferred();
        _maskChanges.Add(area);
    }

    /// <summary>How far across the squares changed areas are kept in, to tell quickly whether a place is in one.</summary>
    private const float ChangeSquare = 32f;

    /// <summary>Builds again the stretches masks changed in, each once however many masks were set.</summary>
    private void ApplyMasks()
    {
        if (!IsInstanceValid(this))
            return;
        // Out of the tree by now (another scene opened in the editor): nothing to draw into, and it
        // is built again in full as it comes back (see _EnterTree), masks and all.
        if (!IsInsideTree())
        {
            _maskChanges.Clear();
            return;
        }

        var changed = new HashSet<(int, int)>();
        foreach (var area in _maskChanges)
        {
            for (var x = (int)MathF.Floor(area.Position.X / ChangeSquare); x <= (int)MathF.Floor(area.End.X / ChangeSquare); x++)
                for (var z = (int)MathF.Floor(area.Position.Y / ChangeSquare); z <= (int)MathF.Floor(area.End.Y / ChangeSquare); z++)
                    changed.Add((x, z));
        }

        _maskChanges.Clear();
        var touched = new HashSet<(string Key, int X, int Z)>();
        foreach (var (key, places) in _placed)
        {
            foreach (var place in places)
            {
                if (changed.Contains(((int)MathF.Floor(place.Origin.X / ChangeSquare), (int)MathF.Floor(place.Origin.Z / ChangeSquare))))
                    touched.Add((key, Chunk(key, place).X, Chunk(key, place).Z));
            }
        }

        BuildChunks(touched);
    }

    /// <summary>Whether foliage at a place, this tall, is under a mask.</summary>
    private bool Masked(Vector3 at, float height = 0f) => !_maskIndex.IsEmpty && _maskIndex.Covers(at, height, buffers: KeepBuffers);

    /// <summary>
    /// Whether buffers (along roads, say: see FoliageMaskShape) keep foliage off, hidden and painted
    /// round: set by the Foliage dock. Changed, every field is built again.
    /// </summary>
    public static bool KeepBuffers
    {
        get => _keepBuffers;
        set
        {
            if (_keepBuffers == value)
                return;
            _keepBuffers = value;
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                foreach (var field in InTree(tree))
                    field.QueueRebuild();
            }
        }
    }

    private static bool _keepBuffers = true;

    /// <summary>How tall a type's foliage is at full size: the top of its meshes, from its foot.</summary>
    private float TypeHeight(string key)
    {
        if (_heights.TryGetValue(key, out var height))
            return height;
        height = 0f;
        foreach (var (mesh, _, at) in _parts.TryGetValue(key, out var parts) ? parts.Meshes : [])
            height = MathF.Max(height, (at * mesh.GetAabb()).End.Y);
        return _heights[key] = height;
    }

    private readonly Dictionary<string, float> _heights = [];

    /// <summary>How tall one place's foliage is.</summary>
    private float Height(string key, Transform3D place) => TypeHeight(key) * Size(place);

    /// <summary>
    /// Takes out of its data everything a mask hides (what was painted where a road or building has
    /// since gone): for when the level is saved. How many were taken out.
    /// </summary>
    public int PruneMasked()
    {
        if (_maskIndex.IsEmpty)
            return 0;
        var gone = 0;
        foreach (var (key, places) in _placed)
            gone += places.RemoveAll(p => Masked(p.Origin, Height(key, p)));
        if (gone > 0)
            Commit();
        return gone;
    }

    private static bool InOutline(Vector2[] outline, Vector2 point)
    {
        var inside = false;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
        {
            var (a, b) = (outline[i], outline[j]);
            if (a.Y > point.Y != b.Y > point.Y && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    // ---- Clearing ------------------------------------------------------------------------------

    [ExportToolButton("Clear", Icon = "Clear")]
    public Callable ClearButton => new(this, MethodName.ClearAll);

    /// <summary>Takes away everything painted in it. One step to undo.</summary>
    public void ClearAll()
    {
        var before = Snapshot();
        if (before.Count == 0)
            return;
        var after = new Godot.Collections.Dictionary<string, float[]>();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction($"Clear {base.Name}");
            undo.AddDoMethod(this, MethodName.Restore, after);
            undo.AddUndoMethod(this, MethodName.Restore, before);
            undo.CommitAction();
            return;
        }
#endif
        Restore(after);
    }

    // ---- Thinning out trees ----------------------------------------------------------------

    [ExportToolButton("Thin Out Trees", Icon = "Remove")]
    public Callable ThinOutTreesButton => new(this, MethodName.ThinOutTrees);

    /// <summary>
    /// Takes away trees whose crowns run into another tree's (each by half its crown at its size),
    /// the bigger kept: so none grow through each other. One step to undo.
    /// </summary>
    public void ThinOutTrees()
    {
        var before = Snapshot();
        // The trees read here; which to take away worked out on a thread; taken away back here.
        var trees = _placed
            .Where(kv => _typesByKey.TryGetValue(kv.Key, out var t) && t.Tree)
            .SelectMany(kv => kv.Value.Select(p => (kv.Key, Place: p, Half: Crown(kv.Key) * Size(p) / 2f)))
            .ToList();
        FoliageJob.Start(this, $"Thin Out Trees ({base.Name})", job =>
        {
        trees = trees.OrderByDescending(t => t.Half).ToList();
        job.SetTotal(trees.Count);
        // Biggest first: each kept unless a kept one is too near; looked up by stretch of ground.
        var reach = trees.Select(t => t.Half).DefaultIfEmpty(1f).Max() * 2f;
        var kept = new Dictionary<(int X, int Z), List<(Vector3 At, float Half)>>();
        var gone = new HashSet<(string Key, Transform3D Place)>();
        foreach (var (key, place, half) in trees)
        {
            var at = place.Origin;
            var cell = ((int)MathF.Floor(at.X / reach), (int)MathF.Floor(at.Z / reach));
            var tooNear = false;
            for (var dx = -1; dx <= 1 && !tooNear; dx++)
            {
                for (var dz = -1; dz <= 1 && !tooNear; dz++)
                {
                    if (kept.TryGetValue((cell.Item1 + dx, cell.Item2 + dz), out var near))
                        tooNear = near.Any(n => Flat(n.At - at) < n.Half + half);
                }
            }

            job.Step();
            if (tooNear)
                gone.Add((key, place));
            else if (kept.TryGetValue(cell, out var list))
                list.Add((at, half));
            else
                kept[cell] = [(at, half)];
        }

        return gone;
        }, gone =>
        {
        if (!IsInstanceValid(this))
            return;
        if (gone.Count == 0)
        {
            GD.Print($"{base.Name}: no trees whose crowns run into each other.");
            return;
        }

        foreach (var (key, places) in _placed)
            places.RemoveAll(p => gone.Contains((key, p)));
        var after = Snapshot();
        GD.Print($"{base.Name}: took away {gone.Count} of {trees.Count} trees, whose crowns ran into a bigger one's.");
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction("Thin out trees");
            undo.AddDoMethod(this, MethodName.Restore, after);
            undo.AddUndoMethod(this, MethodName.Restore, before);
            undo.CommitAction();
            return;
        }
#endif
        Restore(after);
        });
    }

    // ---- Following the ground ---------------------------------------------------------------

    /// <summary>Listens for its ground changing, to keep its foliage on the ground.</summary>
    private void FollowGround()
    {
        Ground()?.Follow(new Callable(this, MethodName.GroundChanged));
    }

    /// <summary>A place moved up or down onto the ground (less the type's sink), where there is ground.</summary>
    private Transform3D OnGround(Transform3D place, FoliageType type)
    {
        if (Ground() is not { } ground)
            return place;
        var height = ground.HeightAt(place.Origin);
        return float.IsNaN(height) ? place : place with { Origin = place.Origin with { Y = height - type.Sink } };
    }

    /// <summary>
    /// The terrain changed in an area: the foliage there moved onto the ground again, its stretches
    /// moved in place (their draws and shapes), with no nodes added or taken away, as those make
    /// the editor go over the whole level again.
    /// </summary>
    private void GroundChanged(Rect2 area)
    {
        foreach (var (key, places) in _placed)
        {
            if (!_typesByKey.TryGetValue(key, out var type))
                continue;
            var touched = new HashSet<(int X, int Z)>();
            for (var i = 0; i < places.Count; i++)
            {
                var at = places[i].Origin;
                if (!area.HasPoint(new Vector2(at.X, at.Z)))
                    continue;
                var moved = OnGround(places[i], type);
                if (moved.Origin.Y == at.Y)
                    continue;
                places[i] = moved;
                touched.Add(Chunk(key, moved));
            }

            foreach (var (x, z) in touched)
                MoveChunk(key, x, z);
        }
    }

    /// <summary>A stretch's draws and shapes put where its places are now, in the order they were built in.</summary>
    private void MoveChunk(string key, int x, int z)
    {
        if (!_chunks.TryGetValue((key, x, z), out var built) || !_parts.TryGetValue(key, out var parts))
            return;
        var here = _placed[key].Where(t => Chunk(key, t) == (x, z) && !Masked(t.Origin, Height(key, t))).ToList();
        var toLocal = GlobalTransform.AffineInverse();
        for (var d = 0; d < built.Draws.Count && d < parts.Meshes.Count; d++)
        {
            var multi = built.Draws[d].Multi;
            for (var i = 0; i < here.Count && i < multi.InstanceCount; i++)
                multi.SetInstanceTransform(i, toLocal * here[i] * parts.Meshes[d].At);
        }

        if (parts.Shapes.Count > 0)
            BodyChanged(key, x, z);
    }

    // ---- Bodies -------------------------------------------------------------------------------

    /// <summary>
    /// How far across each of its bodies is, in metres: one body holds the shapes of all its foliage
    /// in a square this big, whatever their type, so a big forest is not tens of thousands of bodies
    /// (the physics has a limit, and past it the terrain's own bodies are not made).
    /// </summary>
    private const float BodySize = 256f;

    /// <summary>
    /// Its bodies, by square: made straight on the physics server, not as nodes, so their shapes can be
    /// put in while they are out of the world, and the world given each at once (put in a body already
    /// in it, each shape had the physics build the whole body again: minutes for a forest).
    /// </summary>
    private readonly Dictionary<(int X, int Z), Rid> _bodies = [];
    private readonly HashSet<(int X, int Z)> _bodiesChanged = [];

    /// <summary>A stretch of a type built again: the body of its square to be built again, once, at the end of the frame.</summary>
    private void BodyChanged(string key, int x, int z)
    {
        // Not in the editor: it never steps physics, and bodies of thousands of shapes only weigh on it.
        if (Engine.IsEditorHint())
            return;
        var size = _tall.Contains(key) ? TreeChunkSize : ChunkSize;
        var square = ((int)MathF.Floor(x * size / BodySize), (int)MathF.Floor(z * size / BodySize));
        if (_bodiesChanged.Count == 0 && IsInsideTree())
            Callable.From(BuildBodies).CallDeferred();
        _bodiesChanged.Add(square);
    }

    /// <summary>The bodies of the squares changed, each with the shapes of everything in it: all its places gone over once.</summary>
    private void BuildBodies()
    {
        if (!IsInstanceValid(this) || !IsInsideTree() || _bodiesChanged.Count == 0)
            return;
        var changed = _bodiesChanged.ToHashSet();
        _bodiesChanged.Clear();
        var shapes = changed.ToDictionary(c => c, _ => new List<(Shape3D Shape, Transform3D At)>());
        foreach (var (key, places) in _placed)
        {
            if (!_parts.TryGetValue(key, out var parts) || parts.Shapes.Count == 0)
                continue;
            foreach (var place in places)
            {
                var square = ((int)MathF.Floor(place.Origin.X / BodySize), (int)MathF.Floor(place.Origin.Z / BodySize));
                if (!shapes.TryGetValue(square, out var list) || Masked(place.Origin, Height(key, place)))
                    continue;
                foreach (var (shape, at) in parts.Shapes)
                    list.Add((shape, place * at));
            }
        }

        var space = GetWorld3D().Space;
        foreach (var (square, list) in shapes)
        {
            if (!_bodies.TryGetValue(square, out var body))
            {
                if (list.Count == 0)
                    continue;
                body = PhysicsServer3D.BodyCreate();
                PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
                PhysicsServer3D.BodySetCollisionLayer(body, 1);
                PhysicsServer3D.BodySetCollisionMask(body, 1);
                PhysicsServer3D.BodyAttachObjectInstanceId(body, GetInstanceId());
                _bodies[square] = body;
            }

            // Out of the world while its shapes go in, then back in it once.
            PhysicsServer3D.BodySetSpace(body, new Rid());
            PhysicsServer3D.BodyClearShapes(body);
            foreach (var (shape, at) in list)
                PhysicsServer3D.BodyAddShape(body, shape.GetRid(), at);
            if (list.Count > 0)
                PhysicsServer3D.BodySetSpace(body, space);
        }

    }

    // ---- Painting -------------------------------------------------------------------------

    /// <summary>The ground its foliage stands on, if the project gives it one.</summary>
    public IFoliageGround? Ground() => FoliageGround.Finder?.Invoke(this);

    private static readonly Color UnknownGroundColour = new(0.29f, 0.37f, 0.18f);

    /// <summary>The colour of its ground at a place, as the ground gives it, for foliage coloured to match.</summary>
    public Color GroundColourAt(Vector3 at) => Ground()?.ColourAt(at) ?? UnknownGroundColour;

    /// <summary>How high its ground is at a place, and which way is square to it there, if it has ground there.</summary>
    public (float Height, Vector3 Normal)? GroundAt(Vector3 at)
    {
        if (Ground() is not { } ground)
            return null;
        var height = ground.HeightAt(at);
        if (float.IsNaN(height))
            return null;
        var normal = ground.NormalAt(at);
        return (height, normal.IsFinite() && normal.LengthSquared() > 0.1f ? normal.Normalized() : Vector3.Up);
    }

    /// <summary>
    /// Fills a circle with these types up to their density (times a share): new ones put at random
    /// where none of the same type is nearer than its spacing, on the ground, inside its outline, and
    /// (if any are given) only on the ground's textures allowed.
    /// </summary>
    public void Paint(Vector3 centre, float radius, IReadOnlyList<FoliageType> types, float density, Random random, IReadOnlyCollection<string>? allowed = null, FoliageDensityNoise? noise = null, bool buffers = true)
    {
        EnsureData();
        var area = MathF.PI * radius * radius;
        var touched = new HashSet<(string, int, int)>();
        foreach (var type in types.Where(t => t?.Scene is not null))
        {
            var key = Key(type);
            if (!_placed.TryGetValue(key, out var places))
            {
                _placed[key] = places = [];
                _parts[key] = type.Parts();
                if (_parts[key].Meshes.Any(m => m.Mesh.GetAabb().Size.Y > TallHeight))
                    _tall.Add(key);
                else
                    _tall.Remove(key);
            }

            _typesByKey[key] = type;

            var reachOut = radius + MaxSpacing();
            var mine = places.Count(p => Flat(p.Origin - centre) <= radius);
            // What it keeps apart from: its own type, by half its spacing at each one's size; and (if it
            // is a tree) every other tree, by half of each one's crown at its size, so none grow
            // through each other.
            var near = places.Where(p => Flat(p.Origin - centre) <= reachOut).Select(p => (p.Origin, Half: type.Spacing * Size(p) / 2f, ByCrown: false)).ToList();
            if (type.Tree)
            {
                foreach (var (other, otherPlaces) in _placed)
                {
                    if (other == key || !_typesByKey.TryGetValue(other, out var otherType) || !otherType.Tree)
                        continue;
                    var crown = Crown(other);
                    near.AddRange(otherPlaces.Where(p => Flat(p.Origin - centre) <= reachOut).Select(p => (p.Origin, Half: crown * Size(p) / 2f, ByCrown: true)));
                }
            }

            var myCrown = Crown(key);

            var wanted = (int)MathF.Round(area * type.Density / 100f * density) - mine;
            var misses = 0;
            for (var attempt = 0; wanted > 0 && attempt < wanted * 8 && misses < GiveUpAfter; attempt++)
            {
                misses++;
                var angle = random.NextSingle() * MathF.Tau;
                var reach = radius * MathF.Sqrt(random.NextSingle());
                var at = centre + new Vector3(MathF.Cos(angle) * reach, 0f, MathF.Sin(angle) * reach);
                var scale = Mathf.Lerp(type.MinScale, type.MaxScale, random.NextSingle());
                var half = type.Spacing * scale / 2f;
                var crownHalf = myCrown * scale / 2f;
                if (near.Any(p => Flat(p.Origin - at) < (p.ByCrown ? crownHalf : half) + p.Half) || !Inside(at) || !AllowedAt(allowed, at) || GroundAt(at) is not { } ground)
                    continue;
                at.Y = ground.Height - type.Sink;
                // Not under a road, a building, a lot or a square (see SetMasks): only where it fits.
                if (!_maskIndex.IsEmpty && _maskIndex.Covers(at, TypeHeight(key) * scale, painting: true, buffers))
                    continue;
                // Thinner, or none, where the density noise is low: the spot counted as filled all the same.
                if (noise is not null && random.NextSingle() > noise.At(at.X, at.Z))
                {
                    wanted--;
                    continue;
                }

                var up = Vector3.Up.Lerp(ground.Normal, type.FollowSlope).Normalized();
                var basis = new Basis(new Quaternion(Vector3.Up, up));
                if (type.RandomTurn)
                    basis *= new Basis(Vector3.Up, random.NextSingle() * MathF.Tau);
                basis = basis.Scaled(Vector3.One * scale);
                places.Add(new Transform3D(basis, at));
                near.Add((at, half, false));
                misses = 0;
                touched.Add((key, Chunk(key, places[^1]).X, Chunk(key, places[^1]).Z));
                wanted--;
            }
        }

        BuildChunks(touched);
    }

    /// <summary>How many tries in a row may fail before a touch of the brush stops trying: the area is as full as it can get, or nothing may go there.</summary>
    private const int GiveUpAfter = 64;

    /// <summary>Takes away those of these types (or of any) within a circle.</summary>
    public void Erase(Vector3 centre, float radius, IReadOnlyList<FoliageType>? types)
    {
        var keys = types is null ? [.. _placed.Keys] : types.Where(t => t is not null).Select(Key).ToList();
        foreach (var key in keys)
        {
            if (!_placed.TryGetValue(key, out var places))
                continue;
            var gone = places.Where(p => Flat(p.Origin - centre) <= radius && !Masked(p.Origin, Height(key, p))).ToList();
            if (gone.Count == 0)
                continue;
            places.RemoveAll(p => Flat(p.Origin - centre) <= radius && !Masked(p.Origin, Height(key, p)));
            foreach (var (X, Z) in gone.Select(t => Chunk(key, t)).Distinct())
                BuildChunk(key, X, Z);
        }
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>How big a place's foliage is, against its scene: its scale.</summary>
    private static float Size(Transform3D place) => place.Basis.Column0.Length();

    /// <summary>The most any of its types keeps apart, at full size: how far round a brush to look for neighbours.</summary>
    /// <summary>How wide a type's foliage is across, at full size: the widest of its meshes, seen from above.</summary>
    private float Crown(string key)
    {
        if (_crowns.TryGetValue(key, out var crown))
            return crown;
        var box = new Aabb();
        var first = true;
        foreach (var (mesh, _, at) in _parts.TryGetValue(key, out var parts) ? parts.Meshes : [])
        {
            var placed = at * mesh.GetAabb();
            box = first ? placed : box.Merge(placed);
            first = false;
        }

        return _crowns[key] = MathF.Max(box.Size.X, box.Size.Z);
    }

    private readonly Dictionary<string, float> _crowns = [];

    private float MaxSpacing() => _typesByKey.Values.Select(t => t.Spacing * t.MaxScale).DefaultIfEmpty(0f).Max();

    /// <summary>Everything painted, packed: to keep (for undoing) and to write.</summary>
    public Godot.Collections.Dictionary<string, float[]> Snapshot()
    {
        var snapshot = new Godot.Collections.Dictionary<string, float[]>();
        foreach (var (key, places) in _placed)
        {
            if (places.Count > 0)
                snapshot[key] = Pack(places);
        }

        return snapshot;
    }

    /// <summary>Puts everything painted back as it was in a snapshot, writes it and builds it.</summary>
    public void Restore(Godot.Collections.Dictionary<string, float[]> snapshot)
    {
        EnsureData();
        _data!.Placed = WithOwn(snapshot);
        Save();
        Rebuild();
    }

    /// <summary>Writes what is painted to its data file.</summary>
    public void Commit()
    {
        EnsureData();
        _data!.Placed = WithOwn(Snapshot());
        Save();
    }

    /// <summary>
    /// The data file's places with its own types' as given, every other type's kept as they are: other
    /// fields can keep theirs in the same file, and writing this one's must not take theirs away.
    /// </summary>
    private Godot.Collections.Dictionary<string, float[]> WithOwn(Godot.Collections.Dictionary<string, float[]> own)
    {
        var mine = _types.Where(t => t is not null).Select(Key).Concat(_placed.Keys).ToHashSet();
        var placed = new Godot.Collections.Dictionary<string, float[]>();
        foreach (var (key, numbers) in _data!.Placed)
        {
            if (!mine.Contains(key))
                placed[key] = numbers;
        }

        foreach (var (key, numbers) in own)
            placed[key] = numbers;
        return placed;
    }

    private void Save()
    {
        if (_data is not null && !string.IsNullOrEmpty(_data.ResourcePath))
            ResourceSaver.Save(_data);
    }

    /// <summary>Its data, made (and saved as Level-Foliage.res, in <see cref="FoliageGround.DataFolder"/> or beside the level) if it has none yet.</summary>
    private void EnsureData()
    {
        if (_data is not null)
            return;
        _data = new FoliageData();
        var level = (Engine.IsEditorHint() ? GetTree().EditedSceneRoot : GetTree().CurrentScene)?.SceneFilePath;
        if (!string.IsNullOrEmpty(level))
        {
            var folder = FoliageGround.DataFolder ?? level.GetBaseDir();
            // Its own: a field draws all its data holds, so no two may share one.
            var path = folder.PathJoin($"{level.GetFile().GetBaseName()}-{System.Text.RegularExpressions.Regex.Replace(base.Name.ToString(), @"[^A-Za-z0-9_-]+", "-")}-Foliage.res");
            DirAccess.MakeDirRecursiveAbsolute(folder);
            ResourceSaver.Save(_data, path);
            _data = ResourceLoader.Load<FoliageData>(path);
        }

#if TOOLS
        if (Engine.IsEditorHint())
            EditorInterface.Singleton.MarkSceneAsUnsaved();
#endif
    }
}
