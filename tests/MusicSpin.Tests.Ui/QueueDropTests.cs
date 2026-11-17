using MusicSpin.App.Views;

namespace MusicSpin.Tests.Ui;

/// <summary>Where a drop lands in a long queue: before the row whose middle is below the pointer.</summary>
public sealed class QueueDropTests
{
    private const double Height = 48;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(23, 0)] // the top half of the first row
    [InlineData(24, 1)] // its middle: the next row
    [InlineData(48, 1)]
    [InlineData(71, 1)]
    [InlineData(72, 2)]
    public void A_drop_in_the_middle_of_the_list_uses_the_row_middle(double y, int index)
        => Assert.Equal(index, QueueDrop.Index(y, Height, 10));

    [Fact]
    public void A_drop_past_the_last_middle_appends()
    {
        Assert.Equal(10, QueueDrop.Index((9 * Height) + (Height / 2), Height, 10));
        Assert.Equal(10, QueueDrop.Index(10_000, Height, 10));
    }

    [Fact]
    public void An_empty_list_has_nowhere_but_the_start()
        => Assert.Equal(0, QueueDrop.Index(20, Height, 0));
}
