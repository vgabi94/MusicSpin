namespace MusicSpin.App.Services.Taskbar;

/// <summary>
/// The buttons under the window's taskbar thumbnail (#142), in their order. The values are the ids the taskbar sends
/// back with a click; any other id is not ours.
/// </summary>
public enum ThumbButtonId
{
    Previous = 0x4D01,
    PlayPause = 0x4D02,
    Next = 0x4D03,
}

/// <summary>The drawing a thumbnail button shows (the player bar's own icons).</summary>
public enum ThumbGlyph
{
    Previous,
    Play,
    Pause,
    Next,
}

/// <summary>One thumbnail button as the taskbar shows it.</summary>
public sealed record ThumbButton(ThumbButtonId Id, ThumbGlyph Glyph, string ToolTip, bool IsEnabled);

/// <summary>
/// The window's taskbar thumbnail toolbar (#142): Windows' <c>ITaskbarList3</c> thumbnail buttons on the main window. A
/// thin native half that only adds and updates buttons and reports clicks; what the buttons show and what a click does is
/// <see cref="TaskbarThumbnailBridge"/>, tested on every OS with a fake.
/// </summary>
public interface ITaskbarThumbnail : IDisposable
{
    /// <summary>The window's taskbar button exists, so buttons can be added (until the next <see cref="Ready"/>).</summary>
    bool IsReady { get; }

    /// <summary>
    /// The window's taskbar button was made: the first time the window shows, and again after Explorer restarts (the
    /// old buttons are gone then, so they are added again). Raised on the UI thread.
    /// </summary>
    event EventHandler? Ready;

    /// <summary>A thumbnail button was clicked, with its id (not always one of ours). Raised on the UI thread.</summary>
    event EventHandler<int>? Clicked;

    /// <summary>Adds the buttons, once per <see cref="Ready"/>. Throws when the taskbar refuses them.</summary>
    void Add(IReadOnlyList<ThumbButton> buttons);

    /// <summary>Shows a change in the buttons added. Throws when the taskbar refuses it.</summary>
    void Update(IReadOnlyList<ThumbButton> buttons);
}
