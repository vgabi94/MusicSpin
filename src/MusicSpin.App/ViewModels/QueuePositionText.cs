using System.Globalization;

namespace MusicSpin.App.ViewModels;

/// <summary>The queue's position counter: "4 of 11" (1-based), following the current entry. Null when nothing is queued.</summary>
public static class QueuePositionText
{
    public static string? Format(int? index, int length)
        => index is >= 0 && length > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{index.Value + 1:N0} of {length:N0}")
            : null;
}
