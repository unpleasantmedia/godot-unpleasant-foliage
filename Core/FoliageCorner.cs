using Godot;

namespace UnpleasantMedia.UnpleasantFoliage;

/// <summary>
/// A corner of its <see cref="FoliageField"/>'s outline, under the field's Shape node: the corners, in
/// their order there, go round the ground the field's foliage may be on. Moved, added (duplicated) or
/// taken away, the outline follows.
/// </summary>
[Tool]
[GlobalClass]
public partial class FoliageCorner : Node3D
{
    private FoliageField? Field => GetParent()?.GetParent() as FoliageField;

    public override void _EnterTree()
    {
        SetNotifyTransform(true);
        Field?.OutlineChanged();
    }

    public override void _ExitTree() => Field?.OutlineChanged();

    public override void _Notification(int what)
    {
        if (what == NotificationTransformChanged)
            Field?.OutlineChanged();
    }
}
