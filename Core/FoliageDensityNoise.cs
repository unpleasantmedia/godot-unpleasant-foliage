using System;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// How thickly foliage grows from place to place, by a noise laid over the world: thick where the
/// noise is high, thinning out where it is low, and none at all below the openness, so forests have
/// clearings. Each place keeps a piece of foliage only as often as the noise there says; how near
/// two may be is kept to as ever, so it is never thicker than their spacing allows.
/// </summary>
public sealed class FoliageDensityNoise
{
    /// <summary>The noises to pick from, as the Foliage dock lists them (after None).</summary>
    public static readonly (string Name, FastNoiseLite.NoiseTypeEnum Type)[] Noises =
    [
        ("Perlin", FastNoiseLite.NoiseTypeEnum.Perlin),
        ("Simplex", FastNoiseLite.NoiseTypeEnum.SimplexSmooth),
        ("Cellular", FastNoiseLite.NoiseTypeEnum.Cellular),
        ("Value", FastNoiseLite.NoiseTypeEnum.Value),
    ];

    /// <summary>How much of the way from the openness to full the noise thins out over (the edges of clearings).</summary>
    private const float Softness = 0.35f;

    private readonly FastNoiseLite _noise;
    private readonly float _openness;

    /// <param name="size">How far across its patches are, in metres.</param>
    /// <param name="openness">How much of the land is open, from 0 (none) to 1 (all of it).</param>
    public FoliageDensityNoise(FastNoiseLite.NoiseTypeEnum type, float size, int octaves, int seed, float openness)
    {
        _noise = new FastNoiseLite
        {
            NoiseType = type,
            Frequency = 1f / Mathf.Max(size, 1f),
            FractalOctaves = Math.Max(1, octaves),
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            Seed = seed,
        };
        _openness = Mathf.Clamp(openness, 0f, 1f);
    }

    /// <summary>How much of the foliage keeps at a place, from 0 (none: open) to 1 (all of it).</summary>
    public float At(float x, float z)
    {
        // The noise, from about -1 to 1, as a share from 0 to 1.
        var share = Mathf.Clamp(_noise.GetNoise2D(x, z) * 0.5f + 0.5f, 0f, 1f);
        return Mathf.SmoothStep(_openness, Mathf.Min(1f, _openness + Softness), share);
    }

    /// <summary>
    /// Whether foliage may stand at a place, for painting by hand: the ground in cells about this far
    /// across, each given a fixed draw from where it is (and the salt, one for each type), kept as
    /// often as the noise says. Fixed, so painting over the same spot again fills only the same cells
    /// (rolled afresh each time, every touch filled a clearing's edges further, up to full).
    /// </summary>
    public bool Allows(float x, float z, float cell, int salt)
    {
        cell = Mathf.Max(cell, 0.25f);
        var cx = (int)MathF.Floor(x / cell);
        var cz = (int)MathF.Floor(z / cell);
        var keep = At((cx + 0.5f) * cell, (cz + 0.5f) * cell);
        if (keep >= 1f)
            return true;
        if (keep <= 0f)
            return false;
        unchecked
        {
            var h = (uint)cx * 0x8DA6B343u ^ (uint)cz * 0xD8163841u ^ (uint)salt * 0xCB1AB31Fu;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000 < keep;
        }
    }

    /// <summary>How much of the foliage keeps over a circle on the whole (see At), from points spread over it.</summary>
    public float Average(float x, float z, float radius)
    {
        const int Steps = 8;
        var sum = 0f;
        var count = 0;
        for (var i = 0; i <= Steps; i++)
        {
            for (var j = 0; j <= Steps; j++)
            {
                var dx = (i / (float)Steps * 2f - 1f) * radius;
                var dz = (j / (float)Steps * 2f - 1f) * radius;
                if (dx * dx + dz * dz > radius * radius)
                    continue;
                sum += At(x + dx, z + dz);
                count++;
            }
        }

        return count == 0 ? At(x, z) : sum / count;
    }

    /// <summary>A small picture of a noise, for the dock.</summary>
    public static Texture2D Preview(FastNoiseLite.NoiseTypeEnum type) =>
        ImageTexture.CreateFromImage(new FastNoiseLite { NoiseType = type, Frequency = 0.04f, FractalOctaves = 3, FractalType = FastNoiseLite.FractalTypeEnum.Fbm }.GetImage(64, 64));
}
