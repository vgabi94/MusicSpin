using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicSpin.App.ViewModels;

/// <summary>A record standing in a crate: its album and its place from the front record.</summary>
public sealed partial class CrateRecordViewModel(CrateViewModel crate, AlbumItemViewModel album, int index) : ObservableObject
{
    public CrateViewModel Crate { get; } = crate;

    public AlbumItemViewModel Album { get; } = album;

    /// <summary>Its place in the crate's list.</summary>
    public int Index { get; } = index;

    /// <summary>The place from the front record: 0 is the front, 1, 2, … behind it, −1 and less flipped forward.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZIndex), nameof(IsShown), nameof(IsFront))]
    public partial int Position { get; set; }

    /// <summary>Front to back; flipped records on top of all (mockup: z-index 100 − |d| + 50 when flipped).</summary>
    public int ZIndex => 100 - Math.Abs(Position) + (Position < 0 ? 50 : 0);

    /// <summary>Seen, so it takes clicks: the front record and the ones behind it, up to <see cref="Controls.CrateRecord.Deepest"/>.</summary>
    public bool IsShown => Position is >= 0 and <= Controls.CrateRecord.Deepest;

    public bool IsFront => Position == 0;
}

/// <summary>
/// A crate of records (mockup: .crate, layoutCrate, setCrate): its albums in order, which one is at the front, and the
/// records near the front as <see cref="Records"/> (the rest aren't seen, so they aren't made).
/// </summary>
public sealed partial class CrateViewModel : ObservableObject
{
    /// <summary>Records this far forward of the front are kept while they finish falling (they are hidden from −1).</summary>
    private const int KeptFlipped = 3;

    private readonly Dictionary<int, CrateRecordViewModel> _shown = [];

    public CrateViewModel(string key, string? name, string label, IReadOnlyList<AlbumItemViewModel> albums, int front)
    {
        Key = key;
        Name = name;
        Label = label;
        Albums = albums;
        FrontIndex = Math.Clamp(front, 0, Math.Max(0, albums.Count - 1));
        Refresh();
    }

    /// <summary>The group's key, or empty for the one crate.</summary>
    public string Key { get; }

    /// <summary>The group's name on a grouped crate (null on the one crate).</summary>
    public string? Name { get; }

    /// <summary>"A — Z", "1998 — 2024", or the group's count (" · 12") after its <see cref="Name"/>.</summary>
    public string Label { get; }

    public IReadOnlyList<AlbumItemViewModel> Albums { get; }

    /// <summary>The records near the front, in no particular order (their z-index sorts them).</summary>
    public ObservableCollection<CrateRecordViewModel> Records { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Front))]
    public partial int FrontIndex { get; private set; }

    /// <summary>The crate the arrows flip and the side panel shows (mockup: .crate.active).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public AlbumItemViewModel? Front => Albums.Count > 0 ? Albums[FrontIndex] : null;

    /// <summary>"AMBIENT · 2" for a grouped crate's label (mockup: .nm + count).</summary>
    public static string GroupLabel(int count) => string.Create(CultureInfo.CurrentCulture, $" · {count:N0}");

    /// <summary>Whether <paramref name="step"/> records on (+) or back (−) is still in the crate.</summary>
    public bool CanFlip(int step) => step != 0 && FrontIndex + step >= 0 && FrontIndex + step < Albums.Count;

    /// <summary>Brings the record at <paramref name="index"/> to the front.</summary>
    public void Show(int index)
    {
        FrontIndex = Math.Clamp(index, 0, Math.Max(0, Albums.Count - 1));
        Refresh();
    }

    /// <summary>Makes the records near the front and gives each its place; drops those that left.</summary>
    private void Refresh()
    {
        var first = Math.Max(0, FrontIndex - KeptFlipped);
        var last = Math.Min(Albums.Count - 1, FrontIndex + Controls.CrateRecord.Deepest + 1);
        foreach (var (index, record) in _shown.Where(p => p.Key < first || p.Key > last).ToList())
        {
            _shown.Remove(index);
            Records.Remove(record);
        }

        for (var i = first; i <= last; i++)
        {
            if (!_shown.TryGetValue(i, out var record))
            {
                // A record that comes into view starts where it would have stood, so it doesn't fly in from the front.
                record = new CrateRecordViewModel(this, Albums[i], i) { Position = i - FrontIndex };
                _shown[i] = record;
                Records.Add(record);
            }

            record.Position = i - FrontIndex;
        }
    }
}
