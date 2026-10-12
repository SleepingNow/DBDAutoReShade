using System.Windows;
using System.Windows.Automation.Peers;

namespace DBDAutoReShade.App.Controls;

/// <summary>Assistive-technology helpers for the Campfire controls.</summary>
public static class CampfireAutomation
{
    /// <summary>
    /// Raised for every announcement asked for (<see cref="AnnounceLiveRegion"/>), whether or not assistive technology
    /// is listening: a diagnostic hook, so tests can check that a live region speaks (on the element's thread).
    /// </summary>
    public static event EventHandler<UIElement>? AnnouncementRequested;

    /// <summary>
    /// Announces a live region (an element with <c>AutomationProperties.LiveSetting</c> Polite or Assertive) after its
    /// content changed: WPF only reads a live region when its automation peer raises
    /// <see cref="AutomationEvents.LiveRegionChanged"/>. Does nothing when no assistive technology is listening.
    /// Returns true when the event was raised.
    /// </summary>
    public static bool AnnounceLiveRegion(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        AnnouncementRequested?.Invoke(null, element);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return false;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        if (peer is null)
        {
            return false;
        }

        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }
}
