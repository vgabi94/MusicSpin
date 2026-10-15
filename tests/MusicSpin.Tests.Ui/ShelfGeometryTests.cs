using Avalonia;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;

namespace MusicSpin.Tests.Ui;

/// <summary>The shelf's slots and the 3D projection behind the shelf and the crates.</summary>
public sealed class ShelfGeometryTests
{
    private const double Size = 170;

    /// <summary>A whole pixel, as layout rounds each slot.</summary>
    private static double Whole(double width) => Math.Ceiling(width);

    [Theory]
    [InlineData(58)]
    [InlineData(20)]
    [InlineData(45)]
    [InlineData(80)]
    public void A_board_holds_as_many_closed_sleeves_as_fit_beside_a_hovered_and_a_playing_one(double tilt)
    {
        var (closed, hovered, playing) = (Whole(ShelfGeometry.SlotWidth(Size, tilt, 0, 0)),
            Whole(ShelfGeometry.SlotWidth(Size, tilt, 1, 0)), Whole(ShelfGeometry.SlotWidth(Size, tilt, 1, 1)));
        for (var width = 300; width <= 4000; width += 7)
        {
            var n = ShelfGeometry.PerBoard(width, Size, tilt);

            Assert.True(n >= 1);
            if (width >= hovered + playing - closed) // room for one sleeve opened all the way, as the playing one is
            {
                // all n fit, one of them hovered and another playing …
                Assert.True(((n - 2) * closed) + hovered + playing <= width, $"{n} at {width} px, {tilt}°");
            }

            // … and one more wouldn't.
            Assert.True(((n - 1) * closed) + hovered + playing > width, $"{n + 1} would fit at {width} px, {tilt}°");
        }
    }

    [Fact]
    public void A_slot_widens_from_closed_to_open_and_more_for_the_playing_record()
    {
        Assert.Equal((Size * Math.Cos(58 * Math.PI / 180)) + 6, ShelfGeometry.SlotWidth(Size, 58, 0, 0), 6);
        Assert.Equal(Size + 30, ShelfGeometry.SlotWidth(Size, 58, 1, 0), 6);
        Assert.Equal((Size * 1.55) + 30, ShelfGeometry.SlotWidth(Size, 58, 1, 1), 6);
        Assert.Equal(91, ShelfGeometry.SlotWidth(Size, 60, 0, 0)); // no float noise to round up a pixel
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 2680)]
    [InlineData(100, 700)]
    public void Depth_sets_the_perspective_as_the_mockup_does(double depth, double distance)
        => Assert.Equal(distance, ShelfGeometry.Perspective(depth), 6);

    [Fact]
    public void A_turn_about_the_left_edge_keeps_that_edge_and_foreshortens_the_right_one()
    {
        var turn = Projection.About(new Point(0, Size / 2), Projection.RotateY(60));

        Assert.Equal(0, turn.Apply(0, 0).X, 6);
        Assert.Equal(0, turn.Apply(0, 0).Y, 6);
        Assert.Equal(Size / 2, turn.Apply(Size, Size).X, 6); // flat: cos 60°
    }

    [Fact]
    public void Perspective_makes_the_far_side_smaller_toward_the_eye()
    {
        // The sleeve's right edge turned away; the eye at its bottom middle: the bottom edge stays level, the top slopes down.
        var sleeve = Projection.Perspective(1000, new Point(Size / 2, Size)) * Projection.About(new Point(0, Size / 2), Projection.RotateY(60));

        Assert.Equal(Size, sleeve.Apply(Size, Size).Y, 6);
        Assert.True(sleeve.Apply(Size, 0).Y > 0, $"{sleeve.Apply(Size, 0)}");
        Assert.True(sleeve.Apply(Size, Size).Y - sleeve.Apply(Size, 0).Y < Size, "the far edge looks shorter");
    }

    [Fact]
    public void The_2D_matrix_draws_points_where_the_projection_puts_them()
    {
        var projection = Projection.Perspective(800, new Point(50, -200))
                         * Projection.About(new Point(50, 100), Projection.Translate(0, -20, 30) * Projection.RotateX(-12));
        var matrix = projection.ToMatrix();

        foreach (var (x, y) in new[] { (0.0, 0.0), (100.0, 0.0), (37.0, 81.0), (100.0, 100.0) })
        {
            var expected = projection.Apply(x, y);
            var w = (matrix.M13 * x) + (matrix.M23 * y) + matrix.M33;
            Assert.Equal(expected.X, ((matrix.M11 * x) + (matrix.M21 * y) + matrix.M31) / w, 6);
            Assert.Equal(expected.Y, ((matrix.M12 * x) + (matrix.M22 * y) + matrix.M32) / w, 6);
        }
    }
}
