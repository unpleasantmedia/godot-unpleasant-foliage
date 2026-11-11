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

    /// <summary>A small picture of a noise, for the dock.</summary>
    public static Texture2D Preview(FastNoiseLite.NoiseTypeEnum type) =>
        ImageTexture.CreateFromImage(new FastNoiseLite { NoiseType = type, Frequency = 0.04f, FractalOctaves = 3, FractalType = FastNoiseLite.FractalTypeEnum.Fbm }.GetImage(64, 64));
}
