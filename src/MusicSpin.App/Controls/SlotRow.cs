using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

namespace MusicSpin.App.Controls;

/// <summary>
/// A row of items (a row of cards, a shelf board's sleeves) that keeps the controls it built for them (#120). The albums
/// page's rows are recycled as it scrolls: given other items, a row only hands each of its controls its new item, so
/// scrolling builds and styles no new cards or sleeves, as an ItemsControl would. A control without an item (a group's
/// last, shorter row) is hidden, its item let go, and kept for the next row that needs it. Out of the window, the row lets
/// go of all its items (their covers are held while bound), and takes them again when it is back.
/// </summary>
public sealed class SlotRow : Decorator
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty = AvaloniaProperty.Register<SlotRow, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty = AvaloniaProperty.Register<SlotRow, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<ITemplate<Panel?>?> ItemsPanelProperty = AvaloniaProperty.Register<SlotRow, ITemplate<Panel?>?>(nameof(ItemsPanel));

    private Panel? _panel;

    /// <summary>The row's items, each shown by a control from <see cref="ItemTemplate"/>.</summary>
    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>The panel the item controls are laid out in (a StackPanel unless set).</summary>
    public ITemplate<Panel?>? ItemsPanel
    {
        get => GetValue(ItemsPanelProperty);
        set => SetValue(ItemsPanelProperty, value);
    }

    /// <summary>How many item controls it built (tests check scrolling builds none).</summary>
    internal int Built { get; private set; }

    /// <summary>The controls showing an item, in order.</summary>
    internal IEnumerable<Control> Shown => _panel?.Children.Where(c => c.IsVisible) ?? [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ItemsPanelProperty || change.Property == ItemTemplateProperty)
        {
            LetGo(0);
            _panel = null; // built again for the new panel or template
            Fill();
        }
        else if (change.Property == ItemsProperty)
        {
            Fill();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Fill();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LetGo(0);
    }

    /// <summary>
    /// Gives each control its item, building only the controls it doesn't have yet. Only while in the window, as an
    /// ItemsControl: a control built before can't find its ancestors ($parent[UserControl] in the template), even once
    /// it is in the window.
    /// </summary>
    private void Fill()
    {
        if (!this.IsAttachedToVisualTree())
        {
            LetGo(0);
            return;
        }

        if (_panel is null)
        {
            _panel = ItemsPanel?.Build() ?? new StackPanel();
            Child = _panel;
        }

        var items = Items?.Cast<object?>().ToList() ?? [];
        var children = _panel.Children;
        for (var i = 0; i < items.Count; i++)
        {
            if (i == children.Count)
            {
                if (ItemTemplate?.Build(items[i]) is not { } built)
                {
                    break;
                }

                Built++;
                built.DataContext = items[i]; // before it joins the row: it never sees the row's own data context
                children.Add(built);
            }

            children[i].DataContext = items[i];
            children[i].IsVisible = true;
        }

        LetGo(items.Count);
    }

    /// <summary>Hides the controls from <paramref name="from"/> on and lets go of their items.</summary>
    private void LetGo(int from)
    {
        if (_panel is null)
        {
            return;
        }

        for (var i = from; i < _panel.Children.Count; i++)
        {
            _panel.Children[i].IsVisible = false;
            _panel.Children[i].DataContext = null;
        }
    }
}
