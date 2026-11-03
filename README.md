# UnpleasantFoliage

A foliage painting plugin for Godot 4.7 (.NET version). A `FoliageField` draws scenes you paint over the ground in large batches and makes them solid, and brushes in the 3D view paint and erase them.

## Features

- Paint and erase brushes with a radius and density, one undo step per stroke
- Any scene can be a foliage type: its meshes are drawn in batches and its collision shapes are added too
- Drawn and made solid in 32 m stretches, so those out of sight are left out
- Foliage follows the ground when it changes
- Grass can take its colour from the ground under it, with the included `FoliageGrass` shader
- Each type can be limited to some of the ground's textures
- Masks: other code can hide foliage in an area, and bring it back
- Works with any ground: your project connects its own

## Requirements

- The **.NET version** of Godot 4.7
- .NET 8 SDK and C# 12
- A C# project: this plugin can't run in a project without one

## Install

The plugin must end up at exactly `addons/unpleasant_foliage`. As a submodule:

```bash
git submodule add https://github.com/unpleasantmedia/godot-unpleasant-foliage.git addons/unpleasant_foliage
```

Then, in Godot:

1. If the project has no `.csproj` file, choose **Project > Tools > C# > Create C# solution**.
2. Click **Build** (the hammer at the top right).
3. Enable **UnpleasantFoliage** under **Project > Project Settings > Plugins**.

If you see `Unable to load addon script from path`, the project hasn't been built yet: build it, then enable the plugin again.

## Connect your ground

The plugin knows no terrain itself. Implement `IFoliageGround` for your ground and give the plugin a finder when your code loads:

```csharp
internal static class FoliageHook
{
    [ModuleInitializer]
    internal static void Hook() =>
        FoliageGround.Finder = static field => new MyGround(field.Terrain);
}
```

`field.Terrain` is the node set in the field's **Terrain** property, if any. A ground is required to paint: without one, the brush places nothing, because it has no height to put foliage at.

Set `FoliageGround.DataFolder` to choose where new data files go. By default they go beside the level.

`IFoliageGround` also gives the texture names (`TextureNames`) and how much of the ground at a place shows some of them, from 0 to 1 (`ShareOf`). If your ground has no textures, return an empty list and null.

## Use

1. Make a scene for each kind of foliage, and a `FoliageType` resource for it.
2. Add a `FoliageField` to your level and add the types to its **Types**.
3. Select the field, pick types in the **Foliage** dock, and choose **Paint** or **Erase** in the 3D view's toolbar.
4. Drag on the ground. Ctrl-drag erases whichever tool is on. With **All** ticked, Erase takes away every type.

A type's **Name** is what the dock and the field's list show. Its **Allowed Textures** are ticked from the ground of the selected field, or of the first field in the open scene. A type is only painted where nothing but ticked textures shows, so in a fade it only goes between two ticked textures; with none ticked it can go anywhere. Foliage that's already painted stays where it is.

What you paint is saved to a `FoliageData` file of its own, made the first time you paint.

To colour foliage from the ground, use a material with its vertex colour as albedo, or one with the `ground_colour` metadata set to true, such as a `ShaderMaterial` using `Shaders/FoliageGrass.gdshader`.

## Masks

Other plugins can hide foliage in an area, in world X and Z. Hidden foliage isn't drawn and isn't solid, can't be erased or painted over with the brush, and stays in the data, so it returns when the mask goes:

```csharp
foreach (var field in FoliageField.InTree(GetTree()))
{
    field.SetMask("road:12", outline);                       // any polygon, as a Vector2[]
    field.SetMaskRect("plot:3", new Rect2(10, 20, 30, 40));
    field.SetMaskCircle("well", new Vector2(5, 5), 8f);
    field.RemoveMask("well");
}
```

Setting a name again moves that mask, and changes made in the same frame are built once. Masks aren't saved, so set them each time your code loads, in the game as well as in the editor. Set one on each field that needs it, including any that load later.

## License

MIT
