using Avalonia.Threading;

namespace MusicSpin.App.Services;

/// <summary>Runs view-model updates on the UI thread (player events arrive on background threads).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
