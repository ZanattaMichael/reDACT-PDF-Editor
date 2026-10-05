namespace PdfEditor.Core.Pdf;

/// <summary>An axis-aligned rectangle in PDF user space (y grows upward).</summary>
internal readonly record struct PdfRect(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Bottom => Y;
    public float Right => X + Width;
    public float Top => Y + Height;

    /// <summary>The rectangle spanned by two corners, whichever order they come in.</summary>
    public static PdfRect FromCorners(double x0, double y0, double x1, double y1)
    {
        double llx = Math.Min(x0, x1), lly = Math.Min(y0, y1);
        return new PdfRect((float)llx, (float)lly, (float)Math.Abs(x1 - x0), (float)Math.Abs(y1 - y0));
    }

    /// <summary>Reads a PDF rectangle array (<c>[llx lly urx ury]</c>, corners in any order); null when malformed.</summary>
    public static PdfRect? FromArray(PdfArray? array)
    {
        if (array == null || array.Count < 4) return null;
        var v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (array.GetAsNumber(i) is not { } n) return null;
            v[i] = n.Value;
        }
        return FromCorners(v[0], v[1], v[2], v[3]);
    }

    public PdfArray ToArray() => new(Left, Bottom, Right, Top);

    /// <summary>Strict overlap: rectangles that only share an edge do not intersect.</summary>
    public bool Intersects(PdfRect other) =>
        Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    /// <summary>The overlap of two rectangles, or null when they do not overlap.</summary>
    public PdfRect? Intersect(PdfRect other)
    {
        float l = Math.Max(Left, other.Left), b = Math.Max(Bottom, other.Bottom);
        float r = Math.Min(Right, other.Right), t = Math.Min(Top, other.Top);
        return r > l && t > b ? new PdfRect(l, b, r - l, t - b) : null;
    }

    public PdfRect Union(PdfRect other) => FromCorners(
        Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom),
        Math.Max(Right, other.Right), Math.Max(Top, other.Top));
}

/// <summary>
/// A PDF transformation matrix <c>[a b c d e f]</c>, used with row vectors as the specification
/// does: a point (x, y) maps to (a·x + c·y + e, b·x + d·y + f), and <c>m1.Multiply(m2)</c> means
/// "apply m1, then m2".
/// </summary>
internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>Reads a six-number array; the identity when it is missing or malformed.</summary>
    public static Matrix FromArray(PdfArray? array)
    {
        if (array == null || array.Count != 6) return Identity;
        var v = new double[6];
        for (int i = 0; i < 6; i++)
        {
            if (array.GetAsNumber(i) is not { } n) return Identity;
            v[i] = n.Value;
        }
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    /// <summary>This transform followed by <paramref name="next"/>.</summary>
    public Matrix Multiply(Matrix next) => new(
        A * next.A + B * next.C,
        A * next.B + B * next.D,
        C * next.A + D * next.C,
        C * next.B + D * next.D,
        E * next.A + F * next.C + next.E,
        E * next.B + F * next.D + next.F);

    public (double X, double Y) Transform(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transforms a direction (ignores the translation).</summary>
    public (double X, double Y) TransformVector(double x, double y) => (A * x + C * y, B * x + D * y);

    public double Determinant => A * D - B * C;

    /// <summary>The inverse transform, or null when the matrix is singular.</summary>
    public Matrix? Inverse()
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-12) return null;
        return new Matrix(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }

    /// <summary>The bounding box of a rectangle after transformation.</summary>
    public PdfRect TransformRect(double llx, double lly, double urx, double ury)
    {
        var p1 = Transform(llx, lly);
        var p2 = Transform(urx, lly);
        var p3 = Transform(llx, ury);
        var p4 = Transform(urx, ury);
        double minX = Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X));
        double maxX = Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X));
        double minY = Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y));
        double maxY = Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y));
        return PdfRect.FromCorners(minX, minY, maxX, maxY);
    }

    public PdfRect TransformRect(PdfRect r) => TransformRect(r.Left, r.Bottom, r.Right, r.Top);

    public PdfArray ToArray() => new(A, B, C, D, E, F);
}
