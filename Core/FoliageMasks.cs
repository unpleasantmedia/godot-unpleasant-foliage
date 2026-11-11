using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// A shape foliage is kept out of: an outline in world X and Z, between two heights. Foliage is under
/// it where it stands inside the outline and reaches anywhere between those heights (from its foot to
/// its top): so a viaduct's deck (from its underside up) leaves room under it for what is short
/// enough, and a tunnel (up to its roof) leaves the ground over it free.
/// </summary>
/// <para>A buffer (one along a road, or round a building: <paramref name="Buffer"/> names which kind) counts only while that kind is kept (see FoliageField.KeptBuffers).</para>
public readonly record struct FoliageMaskShape(Vector2[] Outline, float Bottom = float.NegativeInfinity, float Top = float.PositiveInfinity, string? Buffer = null);

/// <summary>
/// The masks of a field, by name, looked up by square so that a place is checked only against the
/// shapes near it: there can be thousands (every stretch of every road). A copy can be taken to use
/// on another thread (see <see cref="Copy"/>).
/// </summary>
public sealed class FoliageMaskIndex
{
    /// <summary>How far across each square of the lookup is, in metres.</summary>
    private const float Square = 64f;

    private sealed record Shape(Vector2[] Outline, Rect2 Bounds, float Bottom, float Top, string? Buffer);

    private readonly Dictionary<string, List<Shape>> _byName = [];
    private readonly Dictionary<(int, int), List<Shape>> _bySquare = [];

    public bool IsEmpty => _byName.Count == 0;

    /// <summary>Sets a name's shapes (none: taken away); the areas they covered before and cover now, to build again.</summary>
    public List<Rect2> Set(string name, IReadOnlyList<FoliageMaskShape> shapes)
    {
        var changed = Remove(name);
        var made = shapes.Where(s => s.Outline.Length >= 3).Select(s => new Shape([.. s.Outline], Bounds(s.Outline), s.Bottom, s.Top, s.Buffer)).ToList();
        if (made.Count == 0)
            return changed;
        _byName[name] = made;
        foreach (var shape in made)
        {
            foreach (var square in Squares(shape.Bounds))
            {
                if (!_bySquare.TryGetValue(square, out var list))
                    _bySquare[square] = list = [];
                list.Add(shape);
            }

            changed.Add(shape.Bounds);
        }

        return changed;
    }

    /// <summary>Takes a name's shapes away; the areas they covered.</summary>
    public List<Rect2> Remove(string name)
    {
        if (!_byName.Remove(name, out var old))
            return [];
        foreach (var shape in old)
        {
            foreach (var square in Squares(shape.Bounds))
            {
                if (_bySquare.TryGetValue(square, out var list))
                {
                    list.Remove(shape);
                    if (list.Count == 0)
                        _bySquare.Remove(square);
                }
            }
        }

        return old.Select(s => s.Bounds).ToList();
    }

    /// <summary>
    /// Whether foliage standing at a place, this tall, is under any of them: of the buffers, only those
    /// of the kinds in <paramref name="buffers"/> (all of them if none are given).
    /// </summary>
    public bool Covers(Vector3 at, float height, bool painting = false, IReadOnlyCollection<string>? buffers = null)
    {
        if (_bySquare.Count == 0 || !_bySquare.TryGetValue(SquareOf(at.X, at.Z), out var near))
            return false;
        var point = new Vector2(at.X, at.Z);
        foreach (var shape in near)
        {
            if (shape.Buffer is not null && buffers is not null && !buffers.Contains(shape.Buffer))
                continue;
            if (at.Y >= shape.Top || at.Y + height <= shape.Bottom)
                continue;
            var b = shape.Bounds;
            if (point.X >= b.Position.X && point.Y >= b.Position.Y && point.X <= b.End.X && point.Y <= b.End.Y && InOutline(shape.Outline, point))
                return true;
        }

        return false;
    }

    /// <summary>A copy of it as it is now, to use on another thread while this one goes on changing.</summary>
    public FoliageMaskIndex Copy()
    {
        var copy = new FoliageMaskIndex();
        foreach (var (name, shapes) in _byName)
            copy.Set(name, shapes.Select(s => new FoliageMaskShape(s.Outline, s.Bottom, s.Top, s.Buffer)).ToList());
        return copy;
    }

    private static (int, int) SquareOf(float x, float z) => ((int)MathF.Floor(x / Square), (int)MathF.Floor(z / Square));

    private static IEnumerable<(int, int)> Squares(Rect2 bounds)
    {
        var (fromX, fromZ) = SquareOf(bounds.Position.X, bounds.Position.Y);
        var (toX, toZ) = SquareOf(bounds.End.X, bounds.End.Y);
        for (var x = fromX; x <= toX; x++)
            for (var z = fromZ; z <= toZ; z++)
                yield return (x, z);
    }

    private static Rect2 Bounds(Vector2[] outline)
    {
        var bounds = new Rect2(outline[0], Vector2.Zero);
        foreach (var point in outline)
            bounds = bounds.Expand(point);
        return bounds;
    }

    public static bool InOutline(Vector2[] outline, Vector2 point)
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
}
