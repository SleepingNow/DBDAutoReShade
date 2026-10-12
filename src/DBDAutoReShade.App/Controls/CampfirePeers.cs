using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DBDAutoReShade.App.Controls;

/// <summary>
/// The automation peer of a purely decorative Campfire element (the fog, the Entity's legs, a campfire clearing, map
/// strips and pictures, the skill-check dial): it is neither a control nor content, so screen readers walk past it (the
/// prototype marks these <c>aria-hidden</c>). Its own parts are hidden too, unless <c>keepChildren</c> is set for an
/// element that holds something that speaks (the dial's score event).
/// </summary>
public sealed class DecorativeAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly bool _keepChildren;

    /// <param name="owner">The decorative element.</param>
    /// <param name="keepChildren">Its descendants stay in the tree (they have their own peers to speak with).</param>
    public DecorativeAutomationPeer(FrameworkElement owner, bool keepChildren = false)
        : base(owner) => _keepChildren = keepChildren;

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    /// <inheritdoc />
    protected override string GetClassNameCore() => Owner.GetType().Name;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => false;

    /// <inheritdoc />
    protected override bool IsContentElementCore() => false;

    /// <inheritdoc />
    protected override List<AutomationPeer>? GetChildrenCore() => _keepChildren ? base.GetChildrenCore() : null;
}

/// <summary>
/// A named group's peer (the rail's "Sections" and "At a glance"): a panel or a border has no peer of its own, so its
/// <c>AutomationProperties.Name</c> would never reach assistive technology.
/// </summary>
public sealed class GroupAutomationPeer : FrameworkElementAutomationPeer
{
    /// <param name="owner">The group's element.</param>
    public GroupAutomationPeer(FrameworkElement owner)
        : base(owner)
    {
    }

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    /// <inheritdoc />
    protected override string GetClassNameCore() => Owner.GetType().Name;
}

/// <summary>A <see cref="StackPanel"/> that is a named group for assistive technology (its <c>AutomationProperties.Name</c>).</summary>
public sealed class GroupStackPanel : StackPanel
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupAutomationPeer(this);
}

/// <summary>A <see cref="Border"/> that is a named group for assistive technology (its <c>AutomationProperties.Name</c>).</summary>
public sealed class GroupBorder : Border
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupAutomationPeer(this);
}

/// <summary>
/// The peer of a control that shows one text (a <see cref="CrossfadeText"/>, a <see cref="Keycap"/>): a Text element
/// named by its <c>AutomationProperties.Name</c> when one is set (e.g. "Detection hotkey F8"), else by its text. Its
/// inner text blocks (and a crossfade's leaving ghosts) are not exposed. It carries the owner's heading level and live
/// setting, so a hero text is a heading and a live status line can be announced.
/// </summary>
public sealed class TextControlAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly Func<string> _text;

    /// <param name="owner">The control.</param>
    /// <param name="text">Its text now.</param>
    public TextControlAutomationPeer(FrameworkElement owner, Func<string> text)
        : base(owner) => _text = text ?? throw new ArgumentNullException(nameof(text));

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    /// <inheritdoc />
    protected override string GetClassNameCore() => Owner.GetType().Name;

    /// <inheritdoc />
    protected override string GetNameCore()
    {
        string name = AutomationProperties.GetName(Owner);
        return string.IsNullOrEmpty(name) ? _text() ?? string.Empty : name;
    }

    /// <inheritdoc />
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}
