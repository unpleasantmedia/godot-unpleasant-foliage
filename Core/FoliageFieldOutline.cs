using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// A field's outline: the <see cref="FoliageCorner"/>s under its Shape node, in order. Its foliage is
/// painted only inside it. A field always has one: one without (made before outlines were) is given
/// a rectangle round all it has painted, or a square round itself if it has nothing yet. The outline
/// is drawn in the editor, along the ground, while the field, its Shape or a corner, or the
/// <see cref="FoliageContainer"/> it is in, is selected.
/// </summary>
public partial class FoliageField
{
    /// <summary>How far out from the field a new field's square reaches, in metres.</summary>
    private const float NewOutlineReach = 25f;

    /// <summary>The corners of its outline, in order.</summary>
    public IReadOnlyList<FoliageCorner> Corners => GetNodeOrNull("Shape")?.GetChildren().OfType<FoliageCorner>().ToList() ?? [];

    /// <summary>Its outline in world X and Z, as its corners are now (empty with fewer than three).</summary>
    public Vector2[] Outline
    {
        get
        {
            var corners = Corners;
            return corners.Count >= 3 ? [.. corners.Select(c => new Vector2(c.GlobalPosition.X, c.GlobalPosition.Z))] : [];
        }
    }

    private Vector2[]? _outline;
    private Rect2 _outlineBounds;

    /// <summary>Whether a place is inside its outline (with no outline, everywhere is).</summary>
    public bool Inside(Vector3 at)
    {
        if (_outline is null)
        {
            _outline = Outline;
            _outlineBounds = Bounds(_outline);
        }

        if (_outline.Length < 3)
            return true;
        var point = new Vector2(at.X, at.Z);
        return _outlineBounds.Grow(0.001f).HasPoint(point) && InOutline(_outline, point);
    }

    private static Rect2 Bounds(Vector2[] outline)
    {
        if (outline.Length == 0)
            return new Rect2();
        var bounds = new Rect2(outline[0], Vector2.Zero);
        foreach (var point in outline)
            bounds = bounds.Expand(point);
        return bounds;
    }

    /// <summary>How much of what is seen of the ground must be the allowed textures for foliage to go there.</summary>
    private const float AllowedShare = 0.9f;

    /// <summary>Whether foliage may go at a place on its ground, given the textures allowed (none: any); where the ground can't say, it may.</summary>
    public bool AllowedAt(IReadOnlyCollection<string>? allowed, Vector3 at) =>
        allowed is null || allowed.Count == 0 || Ground()?.ShareOf(at, allowed) is not { } share || share >= AllowedShare;

    private bool _outlineQueued;

    /// <summary>A corner moved, added or taken away: the outline found again, and drawn again at the end of the frame.</summary>
    public void OutlineChanged()
    {
        _outline = null;
        if (_outlineQueued || !IsInsideTree())
            return;
        _outlineQueued = true;
        Callable.From(DrawOutline).CallDeferred();
    }

    /// <summary>In the editor: an outline given it now if it has none, and drawn (for the plugin, as a field is selected: one already in the scene as the scripts were reloaded never enters the tree again).</summary>
    public void EnsureOutline()
    {
        if (!Engine.IsEditorHint() || !IsInsideTree())
            return;
        if (Corners.Count < 3)
            OutlineRound();
        else if (_edge is null || !IsInstanceValid(_edge))
            OutlineChanged();
    }

    /// <summary>In the editor: an outline given it if it has none (once its data is built), and drawn.</summary>
    private void OutlineOnEnter()
    {
        if (!Engine.IsEditorHint())
            return;
        Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree())
                return;
            if (Corners.Count < 3)
                OutlineRound();
            OutlineChanged();
        }).CallDeferred();
    }

    /// <summary>
    /// Gives it an outline: a rectangle round everything it has painted (a little out from it), or a
    /// square round itself if it has nothing.
    /// </summary>
    private void OutlineRound()
    {
        var root = GetTree()?.EditedSceneRoot;
        if (root is null || (root != this && !root.IsAncestorOf(this)))
            return;
        var places = _placed.Values.SelectMany(p => p).Select(p => new Vector2(p.Origin.X, p.Origin.Z)).ToList();
        var middle = new Vector2(GlobalPosition.X, GlobalPosition.Z);
        var area = places.Count > 0 ? Bounds([.. places]).Grow(2f) : new Rect2(middle - Vector2.One * NewOutlineReach, Vector2.One * NewOutlineReach * 2f);
        SetOutline([area.Position, area.Position + new Vector2(area.Size.X, 0f), area.End, area.Position + new Vector2(0f, area.Size.Y)]);
        GD.Print($"{base.Name}: given an outline round {(places.Count > 0 ? "all it has painted" : "itself")}; drag its corners (under Shape) into shape.");
#if TOOLS
        EditorInterface.Singleton.MarkSceneAsUnsaved();
#endif
    }

    /// <summary>Its corners made anew, at these places (world X and Z), on the ground.</summary>
    public void SetOutline(IReadOnlyList<Vector2> points)
    {
        var owner = Engine.IsEditorHint() ? GetTree().EditedSceneRoot : Owner;
        if (GetNodeOrNull<Node3D>("Shape") is not { } shape)
        {
            shape = new Node3D { Name = "Shape" };
            AddChild(shape);
            shape.Owner = owner;
        }

        foreach (var corner in Corners)
        {
            shape.RemoveChild(corner);
            corner.QueueFree();
        }

        for (var i = 0; i < points.Count; i++)
        {
            var corner = new FoliageCorner { Name = $"Corner{i + 1}" };
            shape.AddChild(corner);
            var at = new Vector3(points[i].X, GlobalPosition.Y, points[i].Y);
            if (GroundAt(at) is { } ground)
                at.Y = ground.Height;
            corner.GlobalPosition = at;
            corner.Owner = owner;
        }

        OutlineChanged();
    }

    // ---- Drawing the outline ------------------------------------------------------------------

    /// <summary>How far apart the outline's line is set onto the ground, in metres, and how far above it.</summary>
    private const float EdgeStep = 4f;
    private const float EdgeLift = 0.4f;

    private MeshInstance3D? _edge;

    private void DrawOutline()
    {
        _outlineQueued = false;
        if (!Engine.IsEditorHint() || !IsInstanceValid(this) || !IsInsideTree())
            return;
        // Never without one: left with fewer than three corners (taken away, or its Shape), given one again.
        if (Corners.Count < 3)
        {
            OutlineRound();
            _outlineQueued = false;
        }

        if (_edge is null || !IsInstanceValid(_edge))
        {
            _edge = new MeshInstance3D
            {
                Name = "Edge",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                TopLevel = true,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(0.45f, 1f, 0.35f),
                    NoDepthTest = true,
                },
            };
            AddChild(_edge, false, InternalMode.Front);
        }

        _edge.GlobalTransform = Transform3D.Identity;
        var corners = Corners.Select(c => c.GlobalPosition).ToList();
        if (corners.Count < 2)
        {
            _edge.Mesh = null;
            return;
        }

        Vector3 OnGroundEdge(Vector3 at) => (GroundAt(at) is { } ground ? at with { Y = ground.Height } : at) + Vector3.Up * EdgeLift;
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        for (var i = 0; i < corners.Count; i++)
        {
            var (from, to) = (corners[i], corners[(i + 1) % corners.Count]);
            var steps = Math.Max(1, (int)MathF.Ceiling(new Vector2(to.X - from.X, to.Z - from.Z).Length() / EdgeStep));
            for (var k = 0; k < steps; k++)
                mesh.SurfaceAddVertex(OnGroundEdge(from.Lerp(to, (float)k / steps)));
        }

        mesh.SurfaceAddVertex(OnGroundEdge(corners[0]));
        mesh.SurfaceEnd();
        _edge.Mesh = mesh;
        ShowOutline();
    }

    /// <summary>Its outline shown in the editor only while it, something under it, or its container is selected.</summary>
    public void ShowOutline()
    {
        if (_edge is null || !IsInstanceValid(_edge))
            return;
        var shown = false;
#if TOOLS
        if (Engine.IsEditorHint())
        {
            foreach (var selected in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
            {
                if (selected == this || IsAncestorOf(selected) || (selected is FoliageContainer && selected.IsAncestorOf(this)))
                    shown = true;
            }
        }
#endif
        _edge.Visible = shown;
    }

    // ---- Filling --------------------------------------------------------------------------------

    /// <summary>
    /// Filling its outline with these types up to their density (times a share), where there is room:
    /// what is there already kept, new ones put where none is too near (as painting does), only on the
    /// textures allowed (if any are given); thinned out towards its outline over the falloff given
    /// (none at the edge, full density that far in; 0: not thinned). Made here, on the main thread
    /// (all it needs of the scene taken now); <see cref="FillJob.Run"/> then works out the places on
    /// any thread, and <see cref="ApplyFill"/> puts them in, back on the main thread. Null if it has
    /// no outline.
    /// </summary>
    public FillJob? PrepareFill(IReadOnlyList<FoliageType> types, float density, ulong seed, IReadOnlyCollection<string>? allowed, float falloff = 0f, FoliageDensityNoise? noise = null, IReadOnlyCollection<string>? buffers = null)
    {
        var outline = Outline;
        if (outline.Length < 3)
            return null;
        _outline = outline;
        _outlineBounds = Bounds(outline);
        EnsureData();
        var jobs = new List<FillJob.TypeJob>();
        foreach (var type in types.Where(t => t?.Scene is not null))
        {
            var key = Key(type);
            if (!_placed.ContainsKey(key))
            {
                _placed[key] = [];
                _parts[key] = type.Parts();
                if (_parts[key].Meshes.Any(m => m.Mesh.GetAabb().Size.Y > TallHeight))
                    _tall.Add(key);
                else
                    _tall.Remove(key);
            }

            _typesByKey[key] = type;
            Adopt(type);
            // What it keeps apart from, as it is now: its own type, and (a tree) every other tree.
            var others = new List<(Vector3 At, float Half, bool ByCrown)>();
            others.AddRange(_placed[key].Select(p => (p.Origin, type.Spacing * Size(p) / 2f, false)));
            if (type.Tree)
            {
                foreach (var (other, otherPlaces) in _placed)
                {
                    if (other == key || !_typesByKey.TryGetValue(other, out var otherType) || !otherType.Tree)
                        continue;
                    var crown = Crown(other);
                    others.AddRange(otherPlaces.Select(p => (p.Origin, crown * Size(p) / 2f, true)));
                }
            }

            jobs.Add(new FillJob.TypeJob(key, type.Density, type.Spacing, type.MinScale, type.MaxScale, type.Tree, Crown(key),
                type.RandomTurn, type.FollowSlope, type.Sink, others, _placed[key].Select(p => p.Origin).ToList(), TypeHeight(key)));
        }

        var masks = _maskIndex.Copy();
        return new FillJob(this, outline, _outlineBounds, Ground(), allowed?.ToArray() ?? [], masks, density, falloff, seed, jobs, noise, buffers?.ToArray());
    }

    /// <summary>A fill worked out: its places put in, and the stretches they are in built again.</summary>
    public int ApplyFill(FillJob job)
    {
        var touched = new HashSet<(string, int, int)>();
        foreach (var (key, fresh) in job.Results)
        {
            if (!_placed.TryGetValue(key, out var places))
                continue;
            places.AddRange(fresh);
            foreach (var place in fresh)
                touched.Add((key, Chunk(key, place).X, Chunk(key, place).Z));
        }

        BuildChunks(touched);
        return job.Results.Sum(r => r.Places.Count);
    }

    /// <summary>
    /// The work of filling a field, to do on any thread: it touches nothing of the scene but the
    /// ground's samples (which nothing must change meanwhile), and keeps its places to itself until
    /// they are put in (see <see cref="ApplyFill"/>).
    /// </summary>
    public sealed class FillJob
    {
        public sealed record TypeJob(string Key, float Density, float Spacing, float MinScale, float MaxScale, bool Tree, float Crown,
            bool RandomTurn, float FollowSlope, float Sink, List<(Vector3 At, float Half, bool ByCrown)> Others, List<Vector3> Own, float Height);

        public FoliageField Field { get; }
        private readonly Vector2[] _outline;
        private readonly Rect2 _bounds;
        private readonly IFoliageGround? _ground;
        private readonly string[] _allowed;
        private readonly FoliageMaskIndex _masks;
        private readonly float _density, _falloff;
        private readonly Random _random;
        private readonly List<TypeJob> _types;
        private readonly FoliageDensityNoise? _noise;
        private readonly IReadOnlyCollection<string>? _buffers;

        /// <summary>The places found, by type.</summary>
        public List<(string Key, List<Transform3D> Places)> Results { get; } = [];

        /// <summary>How many it means to put down in all, and how many it has gone through so far (for its progress).</summary>
        public long Total { get; private set; }
        private long _done;
        public long Done => System.Threading.Interlocked.Read(ref _done);

        public FillJob(FoliageField field, Vector2[] outline, Rect2 bounds, IFoliageGround? ground, string[] allowed, FoliageMaskIndex masks,
            float density, float falloff, ulong seed, List<TypeJob> types, FoliageDensityNoise? noise = null, IReadOnlyCollection<string>? buffers = null)
        {
            _noise = noise;
            _buffers = buffers;
            Field = field;
            _outline = outline;
            _bounds = bounds;
            _ground = ground;
            _allowed = allowed;
            _masks = masks;
            _density = density;
            _falloff = falloff;
            _random = new Random((int)(seed ^ (seed >> 32)));
            _types = types;
            var area = MathF.Abs(SignedArea(outline));
            Total = types.Sum(t => (long)Math.Max(0, MathF.Round(area * t.Density / 100f * density)));
        }

        /// <summary>How much of what fills a place keeps: less towards the outline, and as the density noise says.</summary>
        private float Keep(Vector3 at)
        {
            var keep = _falloff > 0f ? Mathf.SmoothStep(0f, 1f, EdgeDistance(_outline, new Vector2(at.X, at.Z)) / _falloff) : 1f;
            return _noise is null ? keep : keep * _noise.At(at.X, at.Z);
        }

        /// <summary>Works out the places: on any thread.</summary>
        public void Run()
        {
            var inside = new OutlineRaster(_outline, _bounds);
            var area = MathF.Abs(SignedArea(_outline));
            // Whether the ground is allowed, kept for each of the ground's squares of 1 m: asking the ground
            // is the dearest part of trying a place, and places tried near each other get the same answer.
            var allowedHere = new Dictionary<(int, int), bool>();
            bool Allowed(Vector3 at)
            {
                if (_allowed.Length == 0 || _ground is null)
                    return true;
                var square = ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z));
                if (!allowedHere.TryGetValue(square, out var allowed))
                    allowedHere[square] = allowed = _ground.ShareOf(at, _allowed) is not { } share || share >= AllowedShare;
                return allowed;
            }


            var counted = 0L;
            foreach (var type in _types)
            {
                var cell = MathF.Max(1f, MathF.Max(type.Spacing, type.Tree ? type.Crown : 0f) * type.MaxScale);
                var grid = new Dictionary<(int, int), List<(Vector3 At, float Half, bool ByCrown)>>();
                (int, int) CellOf(Vector3 v) => ((int)MathF.Floor(v.X / cell), (int)MathF.Floor(v.Z / cell));
                void Add(Vector3 v, float half, bool byCrown)
                {
                    var c = CellOf(v);
                    if (!grid.TryGetValue(c, out var list))
                        grid[c] = list = [];
                    list.Add((v, half, byCrown));
                }

                var biggest = 0f;
                foreach (var (at, half, byCrown) in type.Others)
                {
                    Add(at, half, byCrown);
                    biggest = MathF.Max(biggest, half);
                }

                var mine = type.Own.Count(o => inside.Contains(o.X, o.Z));
                var look = Math.Max(1, (int)MathF.Ceiling((MathF.Max(type.Spacing, type.Tree ? type.Crown : 0f) * type.MaxScale / 2f + biggest) / cell));
                var full = (int)MathF.Round(area * type.Density / 100f * _density);
                var wanted = full - mine;
                System.Threading.Interlocked.Add(ref _done, Math.Min(mine, full));
                var fresh = new List<Transform3D>();
                var misses = 0;
                for (var attempt = 0; wanted > 0 && attempt < wanted * 6 && misses < wanted * 2 + 256; attempt++)
                {
                    misses++;
                    var at = new Vector3(_bounds.Position.X + _random.NextSingle() * _bounds.Size.X, 0f, _bounds.Position.Y + _random.NextSingle() * _bounds.Size.Y);
                    if (!inside.Contains(at.X, at.Z))
                        continue;
                    var scale = Mathf.Lerp(type.MinScale, type.MaxScale, _random.NextSingle());
                    var half = type.Spacing * scale / 2f;
                    var crownHalf = type.Crown * scale / 2f;
                    var (cx, cz) = CellOf(at);
                    var crowded = false;
                    for (var dx = -look; dx <= look && !crowded; dx++)
                    {
                        for (var dz = -look; dz <= look && !crowded; dz++)
                        {
                            if (!grid.TryGetValue((cx + dx, cz + dz), out var near))
                                continue;
                            foreach (var p in near)
                            {
                                if (Flat(p.At - at) < (p.ByCrown ? crownHalf : half) + p.Half)
                                {
                                    crowded = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (crowded || !Allowed(at) || _ground is null)
                        continue;
                    var height = _ground.HeightAt(at);
                    if (float.IsNaN(height))
                        continue;
                    var normal = _ground.NormalAt(at);
                    normal = normal.IsFinite() && normal.LengthSquared() > 0.1f ? normal.Normalized() : Vector3.Up;
                    at.Y = height - type.Sink;
                    // Not under a road, a building, a lot or a square: only where it fits.
                    if (!_masks.IsEmpty && _masks.Covers(at, type.Height * scale, painting: true, _buffers))
                        continue;
                    var up = Vector3.Up.Lerp(normal, type.FollowSlope).Normalized();
                    var basis = new Basis(new Quaternion(Vector3.Up, up));
                    if (type.RandomTurn)
                        basis *= new Basis(Vector3.Up, _random.NextSingle() * MathF.Tau);
                    fresh.Add(new Transform3D(basis.Scaled(Vector3.One * scale), at));
                    Add(at, half, false);
                    misses = 0;
                    wanted--;
                    System.Threading.Interlocked.Increment(ref _done);
                }

                // What it could not fit counted as gone through too, so the progress comes to the end.
                counted += full;
                System.Threading.Interlocked.Exchange(ref _done, counted);

                // Thinned out near the outline once it is full: each new one kept only as often as it is far
                // enough in (none at the edge, all of them the falloff in, smoothly between). Thinned while
                // filling, the spots left near the edge were only tried again until filled after all.
                // And by the density noise: thinner where it is low, none where it is open (see FoliageDensityNoise).
                if (_falloff > 0f || _noise is not null)
                    fresh = fresh.Where(p => _random.NextSingle() <= Keep(p.Origin)).ToList();
                Results.Add((type.Key, fresh));
            }
        }
    }

    /// <summary>
    /// What is inside an outline, by squares a few metres across: those wholly in or out answered at
    /// once, and only those an edge runs through tested against the outline itself.
    /// </summary>
    private sealed class OutlineRaster
    {
        private const float Square = 4f;
        private readonly Vector2[] _outline;
        private readonly Rect2 _bounds;
        private readonly int _columns, _rows;

        /// <summary>0 out, 1 in, 2 an edge runs through it.</summary>
        private readonly byte[] _squares;

        public OutlineRaster(Vector2[] outline, Rect2 bounds)
        {
            _outline = outline;
            _bounds = bounds;
            _columns = Math.Max(1, (int)MathF.Ceiling(bounds.Size.X / Square) + 1);
            _rows = Math.Max(1, (int)MathF.Ceiling(bounds.Size.Y / Square) + 1);
            _squares = new byte[_columns * _rows];
            // Each row's middle: in where an odd number of edges have been crossed.
            var crossings = new List<float>();
            for (var row = 0; row < _rows; row++)
            {
                var z = bounds.Position.Y + (row + 0.5f) * Square;
                crossings.Clear();
                for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
                {
                    var (a, b) = (outline[j], outline[i]);
                    if (a.Y > z != b.Y > z)
                        crossings.Add(a.X + (z - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }

                crossings.Sort();
                for (var k = 0; k + 1 < crossings.Count; k += 2)
                {
                    var from = Math.Max(0, (int)MathF.Floor((crossings[k] - bounds.Position.X) / Square));
                    var to = Math.Min(_columns - 1, (int)MathF.Floor((crossings[k + 1] - bounds.Position.X) / Square));
                    for (var column = from; column <= to; column++)
                        _squares[row * _columns + column] = 1;
                }
            }

            // The squares the edges run through: tested exactly.
            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                var (a, b) = (outline[j], outline[i]);
                var steps = Math.Max(1, (int)MathF.Ceiling(a.DistanceTo(b) / (Square * 0.25f)));
                for (var k = 0; k <= steps; k++)
                {
                    var at = a.Lerp(b, (float)k / steps);
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            var (column, row) = ((int)MathF.Floor((at.X - bounds.Position.X) / Square) + dx, (int)MathF.Floor((at.Y - bounds.Position.Y) / Square) + dz);
                            if (column >= 0 && row >= 0 && column < _columns && row < _rows)
                                _squares[row * _columns + column] = 2;
                        }
                    }
                }
            }
        }

        public bool Contains(float x, float z)
        {
            var (column, row) = ((int)MathF.Floor((x - _bounds.Position.X) / Square), (int)MathF.Floor((z - _bounds.Position.Y) / Square));
            if (column < 0 || row < 0 || column >= _columns || row >= _rows)
                return false;
            return _squares[row * _columns + column] switch
            {
                1 => true,
                2 => InOutline(_outline, new Vector2(x, z)),
                _ => false,
            };
        }
    }

    /// <summary>How far a point is from the nearest stretch of an outline, in metres.</summary>
    private static float EdgeDistance(Vector2[] outline, Vector2 point)
    {
        var nearest = float.MaxValue;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            nearest = MathF.Min(nearest, ClosestOnSegment(point, outline[j], outline[i]).DistanceSquaredTo(point));
        return MathF.Sqrt(nearest);
    }

    private static Vector2 ClosestOnSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        return length <= 0f ? a : a + ab * Math.Clamp((point - a).Dot(ab) / length, 0f, 1f);
    }

    private static float SignedArea(Vector2[] outline)
    {
        var sum = 0f;
        for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            sum += (outline[j].X - outline[i].X) * (outline[j].Y + outline[i].Y);
        return sum / 2f;
    }
}
