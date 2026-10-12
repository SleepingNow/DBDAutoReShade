using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DBDAutoReShade.App.Media;

/// <summary>
/// Tool tips and context menus open with the fade or slide Windows asks for: WPF reads their popup animation from the
/// <see cref="SystemParameters.ToolTipPopupAnimationKey"/> and <see cref="SystemParameters.MenuPopupAnimationKey"/>
/// resources, not from any Campfire style. While "Play animations" is off (or Windows' reduced motion is on) this puts
/// <see cref="PopupAnimation.None"/> under both keys in a resource dictionary (the application's), so those popups simply
/// appear; turned back on, the keys are removed and Windows decides again.
/// </summary>
public static class PopupMotion
{
    /// <summary>The popups may not move: animations off or reduced motion.</summary>
    public static bool IsStill(MotionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return !policy.AnimationsEnabled || policy.ReducedMotion;
    }

    /// <summary>Applies <paramref name="policy"/> to <paramref name="resources"/> once (see the class).</summary>
    public static void Apply(MotionPolicy policy, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (IsStill(policy))
        {
            if (!Equals(resources[SystemParameters.ToolTipPopupAnimationKey], PopupAnimation.None))
            {
                resources[SystemParameters.ToolTipPopupAnimationKey] = PopupAnimation.None;
            }

            if (!Equals(resources[SystemParameters.MenuPopupAnimationKey], PopupAnimation.None))
            {
                resources[SystemParameters.MenuPopupAnimationKey] = PopupAnimation.None;
            }
        }
        else
        {
            // (only the keys this put there: removing an absent key changes nothing)
            if (resources.Contains(SystemParameters.ToolTipPopupAnimationKey))
            {
                resources.Remove(SystemParameters.ToolTipPopupAnimationKey);
            }

            if (resources.Contains(SystemParameters.MenuPopupAnimationKey))
            {
                resources.Remove(SystemParameters.MenuPopupAnimationKey);
            }
        }
    }

    /// <summary>
    /// Applies <paramref name="policy"/> to <paramref name="resources"/> now and whenever it changes (on the calling
    /// thread's dispatcher, which must own the dictionary). Returns a token that stops following.
    /// </summary>
    public static IDisposable Follow(MotionPolicy policy, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resources);
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        bool still = IsStill(policy);
        Apply(policy, resources);

        void OnChanged(object? sender, EventArgs e)
        {
            // (the policy changes for every activation too: the dictionary is touched only when the answer changes)
            if (IsStill(policy) == still)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OnChanged(sender, e)));
                return;
            }

            still = IsStill(policy);
            Apply(policy, resources);
        }

        policy.Changed += OnChanged;
        return new Stop(() => policy.Changed -= OnChanged);
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        public void Dispose()
        {
            _stop?.Invoke();
            _stop = null;
        }
    }
}
