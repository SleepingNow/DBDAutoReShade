using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// Verification hooks for the motion layer (tests and the snapshot harness).
/// <see cref="FindAnimatedObjects"/> proves that nothing ticks: it walks a tree and reports every element,
/// transform, brush, effect or geometry that still has an animation clock attached. Halted ambient loops and
/// finished moments remove their clocks, so a quiet tree reports nothing.
/// </summary>
public static class MotionDiagnostics
{
    /// <summary>Ambient loops whose clocks run on the calling thread.</summary>
    public static IReadOnlyList<string> RunningLoops => AmbientLoop.RunningOnThisThread.Select(l => l.Name).ToList();

    /// <summary>Ambient loops running on the calling thread, each with the frames a second it asks for (null: every frame).</summary>
    public static IReadOnlyList<(string Name, int? FrameRate)> RunningLoopRates =>
        AmbientLoop.RunningOnThisThread.Select(l => (l.Name, l.DesiredFrameRate)).ToList();

    /// <summary>One-shot moments playing on the calling thread.</summary>
    public static IReadOnlyList<string> ActiveMoments => MotionMoment.ActiveOnThisThread.Select(m => m.Name).ToList();

    /// <summary>
    /// Every object under <paramref name="root"/> (visual tree, plus the transforms, brushes, effects, masks and
    /// geometries the elements use) that has an animation clock attached, described as "Type.Name/Path".
    /// Empty means no Storyboard or animation clock can be running in that tree.
    /// </summary>
    public static IReadOnlyList<string> FindAnimatedObjects(DependencyObject root)
    {
        var found = new List<string>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, found, seen);
        return found;
    }

    /// <summary>Stops every moment playing on the calling thread (each lands on its rest pose; frame captures release
    /// the moments they paused with this).</summary>
    public static void StopAllMoments()
    {
        foreach (MotionMoment moment in MotionMoment.ActiveOnThisThread.ToList())
        {
            moment.Stop();
        }
    }

    /// <summary>Freezes every moment and ambient loop on the calling thread at <paramref name="time"/> (frame captures).</summary>
    public static void FreezeAllAt(TimeSpan time) => FreezeAllAt(time, null);

    /// <summary>
    /// Freezes the window at <paramref name="time"/> into a change (frame captures): the members of
    /// <paramref name="story"/> each at their own offset inside it (<see cref="StateMoment.FreezeAt"/>), every other
    /// moment and every ambient loop on the calling thread at <paramref name="time"/> from its own start.
    /// </summary>
    public static void FreezeAllAt(TimeSpan time, StateMoment? story)
    {
        story?.FreezeAt(time);
        foreach (MotionMoment moment in MotionMoment.ActiveOnThisThread.ToList())
        {
            if (story is null || !story.Has(moment))
            {
                moment.FreezeAt(time);
            }
        }

        foreach (AmbientLoop loop in AmbientLoop.RunningOnThisThread.ToList())
        {
            loop.FreezeAt(time);
        }
    }

    private static void Walk(DependencyObject node, List<string> found, HashSet<object> seen)
    {
        if (!seen.Add(node))
        {
            return;
        }

        string label = Describe(node);
        if (node is UIElement element)
        {
            if (element.HasAnimatedProperties)
            {
                found.Add(label);
            }

            CheckAnimatable(element.RenderTransform, label + ".RenderTransform", found, seen);
            CheckAnimatable(element.OpacityMask, label + ".OpacityMask", found, seen);
            CheckAnimatable(element.Clip, label + ".Clip", found, seen);
            CheckAnimatable(element.Effect, label + ".Effect", found, seen);
            if (element is FrameworkElement fe)
            {
                CheckAnimatable(fe.LayoutTransform, label + ".LayoutTransform", found, seen);
            }

            switch (element)
            {
                case Shape shape:
                    CheckAnimatable(shape.Fill, label + ".Fill", found, seen);
                    CheckAnimatable(shape.Stroke, label + ".Stroke", found, seen);
                    if (shape is Path path)
                    {
                        CheckAnimatable(path.Data, label + ".Data", found, seen);
                    }

                    break;
                case Border border:
                    CheckAnimatable(border.Background, label + ".Background", found, seen);
                    CheckAnimatable(border.BorderBrush, label + ".BorderBrush", found, seen);
                    break;
                case Panel panel:
                    CheckAnimatable(panel.Background, label + ".Background", found, seen);
                    break;
                case Control control:
                    CheckAnimatable(control.Background, label + ".Background", found, seen);
                    CheckAnimatable(control.Foreground, label + ".Foreground", found, seen);
                    CheckAnimatable(control.BorderBrush, label + ".BorderBrush", found, seen);
                    break;
                case TextBlock text:
                    CheckAnimatable(text.Foreground, label + ".Foreground", found, seen);
                    break;
            }
        }

        int count = 0;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(node);
        }
        catch (InvalidOperationException)
        {
            // Not a Visual / Visual3D.
        }

        for (int i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(node, i), found, seen);
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d && d is Visual)
            {
                Walk(d, found, seen);
            }
        }
    }

    private static void CheckAnimatable(object? value, string label, List<string> found, HashSet<object> seen)
    {
        if (value is not Animatable animatable || animatable.IsFrozen || !seen.Add(animatable))
        {
            return;
        }

        if (animatable.HasAnimatedProperties)
        {
            found.Add(label + "(" + animatable.GetType().Name + ")");
        }

        switch (animatable)
        {
            case TransformGroup group:
                for (int i = 0; i < group.Children.Count; i++)
                {
                    CheckAnimatable(group.Children[i], label + "[" + i + "]", found, seen);
                }

                break;
            case GradientBrush gradient:
                for (int i = 0; i < gradient.GradientStops.Count; i++)
                {
                    CheckAnimatable(gradient.GradientStops[i], label + ".Stop" + i, found, seen);
                }

                CheckAnimatable(gradient.Transform, label + ".Transform", found, seen);
                CheckAnimatable(gradient.RelativeTransform, label + ".RelativeTransform", found, seen);
                break;
            case Brush brush:
                CheckAnimatable(brush.Transform, label + ".Transform", found, seen);
                CheckAnimatable(brush.RelativeTransform, label + ".RelativeTransform", found, seen);
                if (brush is VisualBrush visualBrush && visualBrush.Visual is not null)
                {
                    Walk(visualBrush.Visual, found, seen);
                }

                break;
            case GeometryGroup geometryGroup:
                foreach (Geometry child in geometryGroup.Children)
                {
                    CheckAnimatable(child, label + ".Child", found, seen);
                }

                CheckAnimatable(geometryGroup.Transform, label + ".Transform", found, seen);
                break;
            case Geometry geometry:
                CheckAnimatable(geometry.Transform, label + ".Transform", found, seen);
                break;
            case Effect:
                break;
            case IEnumerable items:
                foreach (object item in items)
                {
                    CheckAnimatable(item, label + ".Item", found, seen);
                }

                break;
        }
    }

    private static string Describe(DependencyObject node)
    {
        string name = node is FrameworkElement { Name: { Length: > 0 } n } ? n : string.Empty;
        return name.Length > 0 ? node.GetType().Name + "#" + name : node.GetType().Name;
    }
}
