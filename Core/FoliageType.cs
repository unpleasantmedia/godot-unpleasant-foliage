using System.Collections.Generic;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// A kind of foliage to paint with a <see cref="FoliageField"/>: a scene (its meshes are drawn at
/// each place painted, and its collision shapes put there), and how it is scattered: how thickly,
/// how big, turned how, leaning with the ground how much.
/// </summary>
[Tool]
[GlobalClass]
public partial class FoliageType : Resource
{
    /// <summary>What it is called, in the Foliage dock; if not set, its file's or its scene's name. Also the resource's, so the field's list shows it.</summary>
    [Export]
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            ResourceName = value;
            EmitChanged();
        }
    }

    private string _name = "";

    /// <summary>What it looks like: its meshes and collision shapes are used, nothing else in it.</summary>
    [Export]
    public PackedScene? Scene { get; set; }

    /// <summary>How many to a 10 m square, painted at full density.</summary>
    [Export(PropertyHint.Range, "0.01,500,0.01,or_greater")]
    public float Density { get; set; } = 4f;

    /// <summary>
    /// Whether it is a tree: trees keep apart from every other tree, not only from their own type,
    /// by half of each one's crown (its width at its size), so none grow through each other.
    /// </summary>
    [Export]
    public bool Tree { get; set; }

    /// <summary>How near two of it may be, at least, in metres (at full size; bigger ones stand further apart).</summary>
    [Export(PropertyHint.Range, "0,20,0.05,or_greater,suffix:m")]
    public float Spacing { get; set; } = 1f;

    /// <summary>How big each is, at least and at most, picked at random between.</summary>
    [Export(PropertyHint.Range, "0.05,5,0.01")]
    public float MinScale { get; set; } = 0.85f;

    [Export(PropertyHint.Range, "0.05,5,0.01")]
    public float MaxScale { get; set; } = 1.15f;

    /// <summary>Whether each is turned round at random.</summary>
    [Export]
    public bool RandomTurn { get; set; } = true;

    /// <summary>How far each leans with the slope of the ground: 0 stands upright, 1 square to the ground.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")]
    public float FollowSlope { get; set; }

    /// <summary>How far each is sunk into the ground, so it does not float on a slope, in metres.</summary>
    [Export(PropertyHint.Range, "0,2,0.01,suffix:m")]
    public float Sink { get; set; } = 0.05f;

    /// <summary>The names of the ground's textures it may be painted on, where one shows most; with none, it may be painted on any.</summary>
    [Export]
    public string[] AllowedTextures { get; set; } = [];

    /// <summary>Whether it may be painted where this texture shows most; where the ground can't say, it may.</summary>
    public bool AllowedOn(string? texture) => AllowedTextures.Length == 0 || texture is null || System.Array.IndexOf(AllowedTextures, texture) >= 0;

    /// <summary>What it is made of, taken from its scene: its meshes and its collision shapes, each placed as in the scene.</summary>
    public (List<(Mesh Mesh, Material? Material, Transform3D At)> Meshes, List<(Shape3D Shape, Transform3D At)> Shapes) Parts()
    {
        var meshes = new List<(Mesh, Material?, Transform3D)>();
        var shapes = new List<(Shape3D, Transform3D)>();
        if (Scene?.Instantiate() is not { } root)
            return (meshes, shapes);

        // Where each node is in the scene, from its root.
        Transform3D Place(Node node)
        {
            var at = Transform3D.Identity;
            for (var n = node; n is not null && n != root; n = n.GetParent())
            {
                if (n is Node3D spatial)
                    at = spatial.Transform * at;
            }

            return at;
        }

        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case MeshInstance3D { Mesh: { } mesh } drawn:
                    meshes.Add((mesh, drawn.MaterialOverride, Place(drawn)));
                    break;
                case CollisionShape3D { Shape: { } shape, Disabled: false } solid when solid.GetParent() is CollisionObject3D:
                    shapes.Add((shape, Place(solid)));
                    break;
            }

            foreach (var child in node.GetChildren())
                stack.Push(child);
        }

        root.Free();
        return (meshes, shapes);
    }
}
