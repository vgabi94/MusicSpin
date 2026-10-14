namespace MusicSpin.App.Services;

/// <summary>Stepping a value to round numbers (the volume's keys and wheel: 37.5 goes to 38 or 37, by 5 to 40 or 35).</summary>
public static class Steps
{
    private const double Tolerance = 1e-9;

    /// <summary>The next whole multiple of |<paramref name="step"/>| above (step &gt; 0) or below (step &lt; 0) <paramref name="value"/>.</summary>
    public static double Next(double value, double step)
    {
        var size = Math.Abs(step);
        if (size == 0)
        {
            return value;
        }

        return step > 0
            ? (Math.Floor((value / size) + Tolerance) * size) + size
            : (Math.Ceiling((value / size) - Tolerance) * size) - size;
    }
}
