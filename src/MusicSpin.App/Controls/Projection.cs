using Avalonia;

namespace MusicSpin.App.Controls;

/// <summary>
/// CSS 3D transforms for flat things (sleeves, crate records): a 4×4 matrix in CSS's conventions (column vectors, y
/// down, z toward the viewer), built from the same functions the mockup uses, then reduced to the projective 2D
/// <see cref="Matrix"/> Avalonia draws with. Avalonia's Rotate3DTransform puts the eye at the element's centre; the
/// mockup's perspective-origin needs it elsewhere (a sleeve's bottom edge, high above a crate), hence this.
/// </summary>
public readonly record struct Projection(
    double M00, double M01, double M02, double M03,
    double M10, double M11, double M12, double M13,
    double M20, double M21, double M22, double M23,
    double M30, double M31, double M32, double M33)
{
    public static Projection Identity { get; } = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public static Projection Translate(double x, double y, double z) => Identity with { M03 = x, M13 = y, M23 = z };

    /// <summary>CSS rotateY: positive turns the right side away from the viewer.</summary>
    public static Projection RotateY(double degrees)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return Identity with { M00 = cos, M02 = sin, M20 = -sin, M22 = cos };
    }

    /// <summary>CSS rotateX: positive tips the top away from the viewer.</summary>
    public static Projection RotateX(double degrees)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return Identity with { M11 = cos, M12 = -sin, M21 = sin, M22 = cos };
    }

    /// <summary>CSS perspective(<paramref name="distance"/>) seen from <paramref name="origin"/>; 0 or less: none (flat).</summary>
    public static Projection Perspective(double distance, Point origin)
        => distance <= 0 ? Identity
            : Translate(origin.X, origin.Y, 0) * (Identity with { M32 = -1 / distance }) * Translate(-origin.X, -origin.Y, 0);

    /// <summary>A transform about <paramref name="origin"/> (CSS transform-origin).</summary>
    public static Projection About(Point origin, Projection transform)
        => Translate(origin.X, origin.Y, 0) * transform * Translate(-origin.X, -origin.Y, 0);

    public static Projection operator *(Projection a, Projection b) => new(
        Dot(a.M00, a.M01, a.M02, a.M03, b.M00, b.M10, b.M20, b.M30), Dot(a.M00, a.M01, a.M02, a.M03, b.M01, b.M11, b.M21, b.M31),
        Dot(a.M00, a.M01, a.M02, a.M03, b.M02, b.M12, b.M22, b.M32), Dot(a.M00, a.M01, a.M02, a.M03, b.M03, b.M13, b.M23, b.M33),
        Dot(a.M10, a.M11, a.M12, a.M13, b.M00, b.M10, b.M20, b.M30), Dot(a.M10, a.M11, a.M12, a.M13, b.M01, b.M11, b.M21, b.M31),
        Dot(a.M10, a.M11, a.M12, a.M13, b.M02, b.M12, b.M22, b.M32), Dot(a.M10, a.M11, a.M12, a.M13, b.M03, b.M13, b.M23, b.M33),
        Dot(a.M20, a.M21, a.M22, a.M23, b.M00, b.M10, b.M20, b.M30), Dot(a.M20, a.M21, a.M22, a.M23, b.M01, b.M11, b.M21, b.M31),
        Dot(a.M20, a.M21, a.M22, a.M23, b.M02, b.M12, b.M22, b.M32), Dot(a.M20, a.M21, a.M22, a.M23, b.M03, b.M13, b.M23, b.M33),
        Dot(a.M30, a.M31, a.M32, a.M33, b.M00, b.M10, b.M20, b.M30), Dot(a.M30, a.M31, a.M32, a.M33, b.M01, b.M11, b.M21, b.M31),
        Dot(a.M30, a.M31, a.M32, a.M33, b.M02, b.M12, b.M22, b.M32), Dot(a.M30, a.M31, a.M32, a.M33, b.M03, b.M13, b.M23, b.M33));

    /// <summary>Where the flat point (<paramref name="x"/>, <paramref name="y"/>, 0) lands on screen.</summary>
    public Point Apply(double x, double y)
    {
        var w = (M30 * x) + (M31 * y) + M33;
        return new Point(((M00 * x) + (M01 * y) + M03) / w, ((M10 * x) + (M11 * y) + M13) / w);
    }

    /// <summary>The 2D matrix for an element in the z = 0 plane (Avalonia multiplies row vectors: [x y 1] × M).</summary>
    public Matrix ToMatrix() => new(M00, M10, M30, M01, M11, M31, M03, M13, M33);

    private static double Dot(double a0, double a1, double a2, double a3, double b0, double b1, double b2, double b3)
        => (a0 * b0) + (a1 * b1) + (a2 * b2) + (a3 * b3);
}
