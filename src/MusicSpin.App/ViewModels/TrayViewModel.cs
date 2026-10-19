using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The tray icon of a background player (#83; <c>MusicSpin --tray</c>). It only connects to a running player, never
/// starts one, follows its status (the Play/Pause label and the tooltip), and sends the player's own commands:
/// <c>toggle</c>, <c>next</c>, <c>prev</c>, <c>quit</c>. When the player is gone (quit, crash, never there) it asks to
/// exit (<see cref="ExitRequested"/>), so the icon goes with the player.
/// </summary>
public sealed partial class TrayViewModel(IPlayerConnector connector, IUiDispatcher ui) : ObservableObject, IAsyncDisposable
{
    public const string AppName = "MusicSpin";

    private readonly CancellationTokenSource _life = new();
    private PlayerClient? _client;
    private PlayerClient? _events;
    private int _exited;

    /// <summary>The player is gone: the tray process should remove its icon and exit. Raised once, on the UI thread.</summary>
    public event EventHandler? ExitRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseHeader))]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial string ToolTip { get; private set; } = AppName;

    public string PlayPauseHeader => IsPlaying ? "Pause" : "Play";

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    /// <summary>Connects to the running player and follows it. False (and <see cref="ExitRequested"/>) when there is none.</summary>
    public async Task<bool> StartAsync()
    {
        try
        {
            _client = await connector.ConnectAsync(_life.Token).ConfigureAwait(false);
            _events = await connector.ConnectAsync(_life.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or OperationCanceledException)
        {
            Exit();
            return false;
        }

        _ = ListenAsync(_events);
        return true;
    }

    [RelayCommand]
    private Task TogglePlayPause() => SendAsync("toggle");

    [RelayCommand]
    private Task Next() => SendAsync("next");

    [RelayCommand]
    private Task Previous() => SendAsync("prev");

    /// <summary>Shuts the player down (as <c>music-spin quit</c>) and exits; when the player is already gone, just exits.</summary>
    [RelayCommand]
    private async Task Quit()
    {
        if (_client is { } client && !HasExited)
        {
            try
            {
                await client.SendAsync("quit", null, _life.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MusicSpinException or PlayerRequestException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // gone already
            }
        }

        Exit();
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _exited, 1);
        await _life.CancelAsync().ConfigureAwait(false);
        await DropAsync().ConfigureAwait(false);
        _life.Dispose();
    }

    /// <summary>
    /// A command. A refusal (e.g. nothing to play) changes nothing; a lost connection means the player is gone, which the
    /// event stream reports too, so the tray exits either way.
    /// </summary>
    private async Task SendAsync(string cmd)
    {
        if (_client is not { } client || HasExited)
        {
            return;
        }

        try
        {
            await client.SendAsync(cmd, null, _life.Token).ConfigureAwait(false);
        }
        catch (PlayerRequestException)
        {
            // refused; the status stays as it is
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or ObjectDisposedException)
        {
            Exit();
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // closing
        }
    }

    private async Task ListenAsync(PlayerClient events)
    {
        try
        {
            var stream = events.SubscribeAsync(false, status => ui.Post(() => Apply(status)), _life.Token).GetAsyncEnumerator(_life.Token);
            await using (stream.ConfigureAwait(false))
            {
                while (await stream.MoveNextAsync().ConfigureAwait(false))
                {
                    await RefreshAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // gone (or closing)
        }

        Exit();
    }

    private async Task RefreshAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            var status = await client.StatusAsync(_life.Token).ConfigureAwait(false);
            ui.Post(() => Apply(status));
        }
        catch (Exception ex) when (ex is MusicSpinException or PlayerRequestException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // the stream ending says whether the player is gone
        }
    }

    private void Apply(PlayerStatus status)
    {
        if (HasExited)
        {
            return;
        }

        IsConnected = true;
        IsPlaying = status.State == PlayerState.Playing;
        ToolTip = Describe(status);
    }

    /// <summary>"MusicSpin", or "MusicSpin: Title – Artist" (paused: "(paused)" after it).</summary>
    internal static string Describe(PlayerStatus status)
    {
        if (status.Item is not { } item || status.State == PlayerState.Stopped)
        {
            return AppName;
        }

        var title = string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileNameWithoutExtension(item.Path) : item.Title;
        var text = string.IsNullOrWhiteSpace(item.Artist) ? $"{AppName}: {title}" : $"{AppName}: {title} – {item.Artist}";
        return status.State == PlayerState.Paused ? $"{text} (paused)" : text;
    }

    private void Exit()
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0)
        {
            return;
        }

        _life.Cancel();
        _ = DropAsync();
        ui.Post(() =>
        {
            IsConnected = false;
            IsPlaying = false;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task DropAsync()
    {
        foreach (var client in new[] { Interlocked.Exchange(ref _client, null), Interlocked.Exchange(ref _events, null) })
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
