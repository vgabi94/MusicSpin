namespace MusicSpin.App.Services;

/// <summary>Something Esc can close: a menu, Settings, the queue drawer, Now Playing.</summary>
public interface ILayer
{
    /// <summary>Higher closes first (see <see cref="LayerOrder"/>).</summary>
    int Order { get; }

    bool IsOpen { get; }

    void Close();
}

/// <summary>Esc closes the topmost open layer, in this order.</summary>
public static class LayerOrder
{
    public const int NowPlaying = 10;
    public const int QueueDrawer = 20;
    public const int QueueSave = 25;
    public const int Settings = 30;
    public const int PlaylistRename = 35;
    public const int Menu = 40;
}

/// <summary>The layers the window has; later features register theirs.</summary>
public sealed class LayerRegistry
{
    private readonly List<ILayer> _layers = [];

    public IReadOnlyList<ILayer> Layers => _layers;

    public void Register(ILayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layers.Add(layer);
    }

    /// <summary>Closes the open layer with the highest order. False when none is open.</summary>
    public bool CloseTopmost()
    {
        var top = _layers.Where(l => l.IsOpen).OrderByDescending(l => l.Order).FirstOrDefault();
        top?.Close();
        return top is not null;
    }
}
