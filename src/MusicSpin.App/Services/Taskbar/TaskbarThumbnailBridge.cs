using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Services.Taskbar;

/// <summary>
/// The platform-neutral half of the taskbar thumbnail buttons (#142): ⏮, play/pause and ⏭ under the window's
/// thumbnail. They run the app menu's Previous, Play/Pause and Next (the player bar's commands, so ⏮ goes back to the
/// start after a few seconds or on the first track), and they wait when those wait: with nothing loaded, all three; at
/// the end of the queue (repeat off, no radio), ⏭. Play/pause shows ⏸ while the music plays.
/// <para>The buttons follow the player's events (every change of state reaches the menu's commands), never a timer, and
/// only a change in what they show is sent. A taskbar call that fails turns them off, logged; the window goes on
/// without them.</para>
/// </summary>
public sealed class TaskbarThumbnailBridge : IDisposable
{
    private readonly AppMenuViewModel _menu;
    private readonly PlayerBarViewModel _bar;
    private readonly ITaskbarThumbnail _thumbnail;
    private readonly AppLog _log;
    private IReadOnlyList<ThumbButton>? _shown;
    private bool _disposed;

    public TaskbarThumbnailBridge(AppMenuViewModel menu, PlayerBarViewModel bar, ITaskbarThumbnail thumbnail, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(thumbnail);
        ArgumentNullException.ThrowIfNull(log);
        _menu = menu;
        _bar = bar;
        _thumbnail = thumbnail;
        _log = log;
        _menu.PropertyChanged += OnChanged;
        _bar.PropertyChanged += OnChanged;
        _menu.PreviousCommand.CanExecuteChanged += OnCanExecuteChanged;
        _menu.TogglePlayCommand.CanExecuteChanged += OnCanExecuteChanged;
        _menu.NextCommand.CanExecuteChanged += OnCanExecuteChanged;
        _thumbnail.Ready += OnReady;
        _thumbnail.Clicked += OnClicked;
        if (_thumbnail.IsReady)
        {
            AddButtons();
        }
    }

    /// <summary>
    /// The buttons as they should look now. Whether each can run comes from the player's state (the menu's own rules),
    /// not from <c>CanExecute</c>, which is also false while a click's command runs: a button would grey out for every
    /// click and back.
    /// </summary>
    public IReadOnlyList<ThumbButton> Buttons =>
    [
        new(ThumbButtonId.Previous, ThumbGlyph.Previous, _bar.PreviousToolTip, _menu.HasTrack),
        new(ThumbButtonId.PlayPause, _menu.Player.IsPlaying ? ThumbGlyph.Pause : ThumbGlyph.Play, _menu.PlayPauseHeader, _menu.HasTrack),
        new(ThumbButtonId.Next, ThumbGlyph.Next, PlayerBarViewModel.NextTip, _menu.HasNext),
    ];

    /// <summary>A taskbar call failed: the buttons stay as they were, and nothing more is sent.</summary>
    public bool IsOff { get; private set; }

    /// <summary>For tests: the command the last click ran (completed when it is done, or when nothing ran).</summary>
    internal Task LastClick { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _menu.PropertyChanged -= OnChanged;
        _bar.PropertyChanged -= OnChanged;
        _menu.PreviousCommand.CanExecuteChanged -= OnCanExecuteChanged;
        _menu.TogglePlayCommand.CanExecuteChanged -= OnCanExecuteChanged;
        _menu.NextCommand.CanExecuteChanged -= OnCanExecuteChanged;
        _thumbnail.Ready -= OnReady;
        _thumbnail.Clicked -= OnClicked;
        _thumbnail.Dispose();
    }

    /// <summary>A new taskbar button (the window showed, or Explorer restarted): it has none of ours yet.</summary>
    private void OnReady(object? sender, EventArgs e)
    {
        _shown = null;
        AddButtons();
    }

    private void AddButtons()
    {
        if (_disposed || IsOff)
        {
            return;
        }

        var buttons = Buttons;
        if (Try(() => _thumbnail.Add(buttons), "add"))
        {
            _shown = buttons;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppMenuViewModel.PlayPauseHeader) or nameof(PlayerBarViewModel.PreviousToolTip))
        {
            Refresh();
        }
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_disposed || IsOff || _shown is null)
        {
            return; // not added yet: the buttons are made as they are then
        }

        var buttons = Buttons;
        if (buttons.SequenceEqual(_shown))
        {
            return;
        }

        if (Try(() => _thumbnail.Update(buttons), "update"))
        {
            _shown = buttons;
        }
    }

    private bool Try(Action call, string what)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsOff = true;
            _log.Write($"The taskbar thumbnail buttons are off: they could not {what} ({ex.Message}).", ex);
            return false;
        }
    }

    private void OnClicked(object? sender, int id)
    {
        IAsyncRelayCommand? command = (ThumbButtonId)id switch
        {
            ThumbButtonId.Previous => _menu.PreviousCommand,
            ThumbButtonId.PlayPause => _menu.TogglePlayCommand,
            ThumbButtonId.Next => _menu.NextCommand,
            _ => null, // not one of ours
        };
        LastClick = !_disposed && command is not null && ((ICommand)command).CanExecute(null) ? command.ExecuteAsync(null) : Task.CompletedTask;
    }
}
