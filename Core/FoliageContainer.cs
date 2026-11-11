using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// Holds a level's <see cref="FoliageField"/>s. Selected, the Foliage toolbar's Autopaint fills every
/// field in it; its Clear takes away all their foliage at once (one step to undo).
/// </summary>
[Tool]
[GlobalClass]
[Icon("res://addons/unpleasant_foliage/Icons/Foliage.svg")]
public partial class FoliageContainer : Node3D
{
    /// <summary>The fields in it, at any depth.</summary>
    public IReadOnlyList<FoliageField> Fields => FindChildren("*", "", true, false).OfType<FoliageField>().ToList();

    [ExportToolButton("Clear All Foliage", Icon = "Clear")]
    public Callable ClearButton => new(this, MethodName.ClearAll);

    /// <summary>Takes away everything painted in all its fields. One step to undo.</summary>
    public void ClearAll()
    {
        var fields = Fields.Where(f => f.Snapshot().Count > 0).ToList();
        if (fields.Count == 0)
            return;
        var empty = new Godot.Collections.Dictionary<string, float[]>();
#if TOOLS
        if (Engine.IsEditorHint())
        {
            var undo = EditorInterface.Singleton.GetEditorUndoRedo();
            undo.CreateAction($"Clear {Name}");
            foreach (var field in fields)
            {
                undo.AddDoMethod(field, FoliageField.MethodName.Restore, empty);
                undo.AddUndoMethod(field, FoliageField.MethodName.Restore, field.Snapshot());
            }

            undo.CommitAction();
            return;
        }
#endif
        foreach (var field in fields)
            field.Restore(empty);
    }
}
