using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// Foliage painted over a level's ground (with the Foliage plugin's brush): for each of its
/// <see cref="Types"/>, the places painted, kept in <see cref="Data"/> (a file of its own). Each
/// type's meshes are drawn at its places, many to a draw (a MultiMesh per mesh, per stretch of
/// ground <see cref="ChunkSize"/> across, so those out of sight are left out), and its collision
/// shapes are put there too, in a static body per stretch.
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_foliage/Icons/Foliage.svg")]
public partial class FoliageField : Node3D
{
    /// <summary>The kinds of foliage it has to paint with.</summary>
    [Export]
    public Godot.Collections.Array<FoliageType> Types
    {
        get => _types;
        set
        {
            _types = value ?? [];
            QueueRebuild();
        }
    }

    private Godot.Collections.Array<FoliageType> _types = [];

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

    /// <summary>Each type's places, by its file, as built.</summary>
    private readonly Dictionary<string, List<Transform3D>> _placed = [];

    /// <summary>What each stretch of each type is built as: its draws and its body.</summary>
    private readonly Dictionary<(string Type, int X, int Z), (List<MultiMeshInstance3D> Draws, StaticBody3D? Body)> _chunks = [];

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
    }

    public override void _ExitTree() => Clear();

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
        foreach (var type in _types.Where(t => t is not null))
        {
            var key = Key(type);
            _parts[key] = type.Parts();
            _typesByKey[key] = type;
            _placed[key] = _data.Placed.TryGetValue(key, out var numbers) ? Unpack(numbers) : [];
            // On the ground as it is now, wherever it was when painted.
            var places = _placed[key];
            for (var i = 0; i < places.Count; i++)
                places[i] = OnGround(places[i], type);
            foreach (var (X, Z) in _placed[key].Select(Chunk).Distinct().ToList())
                BuildChunk(key, X, Z);
        }
    }

    /// <summary>Takes away everything it has built.</summary>
    private void Clear()
    {
        // All it built, including any left from before its scripts were reloaded (it forgets them then).
        foreach (var built in GetChildren(true).Where(c => c is MultiMeshInstance3D or StaticBody3D && c.Name.ToString().StartsWith(BuiltName)))
        {
            RemoveChild(built);
            built.QueueFree();
        }

        _chunks.Clear();
    }

    /// <summary>What the nodes it builds are called, to tell them from anything else under it.</summary>
    private const string BuiltName = "Foliage";

    private static (int X, int Z) Chunk(Transform3D at) => ((int)MathF.Floor(at.Origin.X / ChunkSize), (int)MathF.Floor(at.Origin.Z / ChunkSize));

    /// <summary>Builds one stretch of one type again, from its places there.</summary>
    private void BuildChunk(string key, int x, int z)
    {
        if (_chunks.Remove((key, x, z), out var old))
        {
            foreach (var built in old.Draws.Cast<Node>().Append(old.Body).OfType<Node>())
            {
                RemoveChild(built);
                built.QueueFree();
            }
        }

        if (!_placed.TryGetValue(key, out var all) || !_parts.TryGetValue(key, out var parts))
            return;
        var here = all.Where(t => Chunk(t) == (x, z) && !Masked(t.Origin)).ToList();
        if (here.Count == 0)
            return;

        // Placed as it is (its own place in the world aside): its foliage is where it was painted.
        var toLocal = GlobalTransform.AffineInverse();
        var draws = new List<MultiMeshInstance3D>();
        // Each the colour of the ground under it, where its material takes its colour from it (a
        // standard one using its vertex colour, or one marked ground_colour, as
        // a grass material is): the colour the ground gives there.
        var coloured = parts.Meshes.Any(m => m.Material is BaseMaterial3D { VertexColorUseAsAlbedo: true }
            || m.Material?.GetMeta("ground_colour", false).AsBool() == true);
        var colours = coloured ? here.Select(p => GroundColourAt(p.Origin)).ToList() : null;
        foreach (var (mesh, material, at) in parts.Meshes)
        {
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = coloured, Mesh = mesh, InstanceCount = here.Count };
            for (var i = 0; i < here.Count; i++)
            {
                multi.SetInstanceTransform(i, toLocal * here[i] * at);
                if (colours is not null)
                    multi.SetInstanceColor(i, colours[i]);
            }

            var draw = new MultiMeshInstance3D { Name = BuiltName, Multimesh = multi, MaterialOverride = material };
            AddChild(draw, true, InternalMode.Back);
            draws.Add(draw);
        }

        // Solid as its scene is: a body (at its place in the world) with its shapes at each of its places.
        StaticBody3D? body = null;
        if (parts.Shapes.Count > 0)
        {
            body = new StaticBody3D { Name = BuiltName + "Body" };
            AddChild(body, true, InternalMode.Back);
            body.GlobalTransform = Transform3D.Identity;
            foreach (var place in here)
            {
                foreach (var (shape, at) in parts.Shapes)
                    PhysicsServer3D.BodyAddShape(body.GetRid(), shape.GetRid(), place * at);
            }
        }

        _chunks[(key, x, z)] = (draws, body);
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

    /// <summary>The areas where foliage is hidden, by the name each caller gave: not saved, so a caller sets them each time it loads.</summary>
    private readonly Dictionary<string, (Vector2[] Outline, Rect2 Bounds)> _masks = [];

    /// <summary>The areas masks changed in, to build again at the end of the frame.</summary>
    private readonly List<Rect2> _maskChanges = [];

    /// <summary>
    /// Hides its foliage inside an outline, in world X and Z, until the mask is removed: it is drawn
    /// and solid no more, but stays in its data. Setting a name again moves that mask.
    /// </summary>
    public void SetMask(string name, Vector2[] outline)
    {
        if (outline.Length < 3)
        {
            RemoveMask(name);
            return;
        }

        var bounds = new Rect2(outline[0], Vector2.Zero);
        foreach (var point in outline)
            bounds = bounds.Expand(point);
        if (_masks.TryGetValue(name, out var old))
            MaskChanged(old.Bounds);
        _masks[name] = ([.. outline], bounds);
        MaskChanged(bounds);
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
        if (_masks.Remove(name, out var old))
            MaskChanged(old.Bounds);
    }

    private void MaskChanged(Rect2 area)
    {
        if (_maskChanges.Count == 0 && IsInsideTree())
            Callable.From(ApplyMasks).CallDeferred();
        _maskChanges.Add(area);
    }

    /// <summary>Builds again the stretches masks changed in, each once however many masks were set.</summary>
    private void ApplyMasks()
    {
        var changes = _maskChanges.ToList();
        _maskChanges.Clear();
        var touched = new HashSet<(string Key, int X, int Z)>();
        bool Inside(Vector3 at) => changes.Any(c => c.HasPoint(new Vector2(at.X, at.Z)) || (at.X == c.End.X || at.Z == c.End.Y) && c.Grow(0.001f).HasPoint(new Vector2(at.X, at.Z)));
        foreach (var (key, places) in _placed)
        {
            foreach (var place in places.Where(p => Inside(p.Origin)))
                touched.Add((key, Chunk(place).X, Chunk(place).Z));
        }

        foreach (var (key, x, z) in touched)
            BuildChunk(key, x, z);
    }

    /// <summary>Whether a place is under a mask.</summary>
    private bool Masked(Vector3 at)
    {
        if (_masks.Count == 0)
            return false;
        var point = new Vector2(at.X, at.Z);
        foreach (var (outline, bounds) in _masks.Values)
        {
            if (point.X >= bounds.Position.X && point.Y >= bounds.Position.Y && point.X <= bounds.End.X && point.Y <= bounds.End.Y && InOutline(outline, point))
                return true;
        }

        return false;
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
        var trees = _placed
            .Where(kv => _typesByKey.TryGetValue(kv.Key, out var t) && t.Tree)
            .SelectMany(kv => kv.Value.Select(p => (kv.Key, Place: p, Half: Crown(kv.Key) * Size(p) / 2f)))
            .OrderByDescending(t => t.Half)
            .ToList();
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

            if (tooNear)
                gone.Add((key, place));
            else if (kept.TryGetValue(cell, out var list))
                list.Add((at, half));
            else
                kept[cell] = [(at, half)];
        }

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
                touched.Add(Chunk(moved));
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
        var here = _placed[key].Where(t => Chunk(t) == (x, z) && !Masked(t.Origin)).ToList();
        var toLocal = GlobalTransform.AffineInverse();
        for (var d = 0; d < built.Draws.Count && d < parts.Meshes.Count; d++)
        {
            var multi = built.Draws[d].Multimesh;
            for (var i = 0; i < here.Count && i < multi.InstanceCount; i++)
                multi.SetInstanceTransform(i, toLocal * here[i] * parts.Meshes[d].At);
        }

        if (built.Body is { } body)
        {
            var shapes = parts.Shapes.Count;
            for (var i = 0; i < here.Count; i++)
            {
                for (var s = 0; s < shapes; s++)
                    PhysicsServer3D.BodySetShapeTransform(body.GetRid(), i * shapes + s, here[i] * parts.Shapes[s].At);
            }
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
    /// where none of the same type is nearer than its spacing, on the ground.
    /// </summary>
    public void Paint(Vector3 centre, float radius, IReadOnlyList<FoliageType> types, float density, Random random)
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
            for (var attempt = 0; wanted > 0 && attempt < wanted * 8; attempt++)
            {
                var angle = random.NextSingle() * MathF.Tau;
                var reach = radius * MathF.Sqrt(random.NextSingle());
                var at = centre + new Vector3(MathF.Cos(angle) * reach, 0f, MathF.Sin(angle) * reach);
                var scale = Mathf.Lerp(type.MinScale, type.MaxScale, random.NextSingle());
                var half = type.Spacing * scale / 2f;
                var crownHalf = myCrown * scale / 2f;
                if (near.Any(p => Flat(p.Origin - at) < (p.ByCrown ? crownHalf : half) + p.Half) || Masked(at) || !type.AllowedAt(Ground(), at) || GroundAt(at) is not { } ground)
                    continue;
                at.Y = ground.Height - type.Sink;
                var up = Vector3.Up.Lerp(ground.Normal, type.FollowSlope).Normalized();
                var basis = new Basis(new Quaternion(Vector3.Up, up));
                if (type.RandomTurn)
                    basis *= new Basis(Vector3.Up, random.NextSingle() * MathF.Tau);
                basis = basis.Scaled(Vector3.One * scale);
                places.Add(new Transform3D(basis, at));
                near.Add((at, half, false));
                touched.Add((key, Chunk(places[^1]).X, Chunk(places[^1]).Z));
                wanted--;
            }
        }

        foreach (var (key, x, z) in touched)
            BuildChunk(key, x, z);
    }

    /// <summary>Takes away those of these types (or of any) within a circle.</summary>
    public void Erase(Vector3 centre, float radius, IReadOnlyList<FoliageType>? types)
    {
        var keys = types is null ? [.. _placed.Keys] : types.Where(t => t is not null).Select(Key).ToList();
        foreach (var key in keys)
        {
            if (!_placed.TryGetValue(key, out var places))
                continue;
            var gone = places.Where(p => Flat(p.Origin - centre) <= radius && !Masked(p.Origin)).ToList();
            if (gone.Count == 0)
                continue;
            places.RemoveAll(p => Flat(p.Origin - centre) <= radius && !Masked(p.Origin));
            foreach (var (X, Z) in gone.Select(Chunk).Distinct())
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
        _data!.Placed = snapshot.Duplicate();
        Save();
        Rebuild();
    }

    /// <summary>Writes what is painted to its data file.</summary>
    public void Commit()
    {
        EnsureData();
        _data!.Placed = Snapshot();
        Save();
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
            var path = folder.PathJoin(level.GetFile().GetBaseName() + "-Foliage.res");
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
