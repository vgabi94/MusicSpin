using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace MusicSpin.App.Controls;

/// <summary>
/// The albums page's list of rows (group headers, rows of cards, shelf boards), virtualized. Its containers are recycled
/// by the kind of row (#120): a row of cards scrolling in takes the container of a row of cards that scrolled out, never a
/// header's, and the container keeps its row control (<see cref="RowHost"/>), which keeps its cards (<see cref="SlotRow"/>).
/// An ItemsControl's own containers build the row and its cards again each time.
/// </summary>
public sealed class RowsControl : ItemsControl
{
    /// <summary>Styled as an ItemsControl.</summary>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = item?.GetType();
        return item is not Control;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new RowHost();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is RowHost host)
        {
            host.Show(item, this);
        }
        else
        {
            base.PrepareContainerForItemOverride(container, item, index);
        }
    }

    /// <summary>A recycled row keeps its row control for the next row of its kind; out of the window, it lets go of its albums.</summary>
    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is not RowHost)
        {
            base.ClearContainerForItemOverride(container);
        }
    }
}

/// <summary>
/// A <see cref="RowsControl"/> container: the row control its row's data template built, kept and given each next row
/// of the same kind as its data context.
/// </summary>
public sealed class RowHost : Decorator
{
    /// <summary>How many row controls it built (tests check recycling builds none).</summary>
    internal int Built { get; private set; }

    /// <summary>Shows <paramref name="row"/>: in the row control it has, or one built from the template <paramref name="owner"/> finds.</summary>
    internal void Show(object? row, Control owner)
    {
        if (Child is null || Child.DataContext?.GetType() != row?.GetType())
        {
            if (owner.FindDataTemplate(row)?.Build(row) is not { } built)
            {
                Child = null;
                return;
            }

            Built++;
            built.DataContext = row; // before it joins: it never sees another row
            Child = built;
            return;
        }

        Child.DataContext = row;
    }
}
