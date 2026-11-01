using Avalonia;
using Avalonia.Controls;

namespace MusicSpin.App.Controls;

/// <summary>
/// A <see cref="TextBlock"/> whose tooltip holds all of its text while the text is cut ("…", or lines past MaxLines),
/// and which has no tooltip while all of it shows (#193). Styled as a TextBlock. It owns its tooltip: one set on it is
/// replaced at each arrange. "Cut" is what the text layout cuts (TextTrimming, MaxLines); text that only runs past a
/// clipping parent (NoWrap without trimming) isn't.
/// </summary>
public class TippedText : TextBlock
{
    /// <summary>Whether the text was cut at the last arrange.</summary>
    public static readonly DirectProperty<TippedText, bool> IsCutProperty =
        AvaloniaProperty.RegisterDirect<TippedText, bool>(nameof(IsCut), o => o.IsCut);

    private bool _isCut;

    public bool IsCut
    {
        get => _isCut;
        private set => SetAndRaise(IsCutProperty, ref _isCut, value);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        IsCut = Cut();
        ToolTip.SetTip(this, IsCut ? Text : null);
        return size;
    }

    private bool Cut()
    {
        if (string.IsNullOrEmpty(Text))
        {
            return false;
        }

        var lines = TextLayout.TextLines;
        var shown = 0;
        foreach (var line in lines)
        {
            if (line.HasCollapsed)
            {
                return true;
            }

            shown += line.Length;
        }

        // Lines past MaxLines are left out without an ellipsis when there is no trimming.
        return shown < Text.Length;
    }
}
