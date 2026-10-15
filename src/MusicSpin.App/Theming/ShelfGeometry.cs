namespace MusicSpin.App.Theming;

/// <summary>
/// The shelf's slots (mockup: .slot, shelfPerRow). A sleeve of size <c>w</c> turned away by the tilt takes
/// <c>w·cos(tilt) + gap</c>; hovered it opens to <c>w + open</c>, and the playing one to <c>1.55·w + open</c>. A board
/// holds as many closed slots as fit while leaving room for one hovered and one playing slot, so nothing reflows when
/// they open.
/// </summary>
public static class ShelfGeometry
{
    /// <summary>The space after a closed slot (mockup: + 6px).</summary>
    public const double SlotGap = 6;

    /// <summary>What an open slot has besides its sleeve (mockup: + 30px).</summary>
    public const double OpenGap = 30;

    /// <summary>The playing slot's extra width, as a share of the sleeve: room for the record half out (mockup: 1.55).</summary>
    public const double PlayingExtra = 0.55;

    /// <summary>A closed slot's width.</summary>
    public static double Step(double size, double tiltDegrees) => (size * Math.Cos(tiltDegrees * Math.PI / 180)) + SlotGap;

    /// <summary>
    /// A slot's width, <paramref name="turn"/> of the way from closed to open (hovered), plus <paramref name="pull"/> of
    /// the playing slot's extra.
    /// </summary>
    public static double SlotWidth(double size, double tiltDegrees, double turn, double pull)
    {
        var step = Step(size, tiltDegrees);
        return Math.Round(step + (turn * (size + OpenGap - step)) + (pull * PlayingExtra * size), Decimals);
    }

    /// <summary>
    /// How many sleeves a board <paramref name="width"/> wide holds (at least one). Layout rounds each slot up to a whole
    /// pixel, so this counts them that way.
    /// </summary>
    public static int PerBoard(double width, double size, double tiltDegrees)
    {
        var step = Whole(SlotWidth(size, tiltDegrees, 0, 0));
        var open = (Whole(SlotWidth(size, tiltDegrees, 1, 0)) - step) + (Whole(SlotWidth(size, tiltDegrees, 1, 1)) - step);
        return Math.Max(1, (int)Math.Floor((width - open) / step));
    }

    /// <summary>Widths are kept to this many decimals, so float noise (91.000000000000014) doesn't round up a pixel.</summary>
    private const int Decimals = 6;

    private static double Whole(double width) => Math.Ceiling(width);

    /// <summary>The perspective distance for Settings › Shelf › Depth (mockup: 4000 − 33·depth px; 0 is flat).</summary>
    public static double Perspective(double depth) => depth > 0 ? 4000 - (33 * depth) : 0;
}
