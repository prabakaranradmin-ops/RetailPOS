using System.Windows;
using System.Windows.Media;

namespace Pos.App.Charts;

/// <summary>The shapes the charts are made of: a smooth line, a bar with rounded shoulders, a ring slice.</summary>
internal static class ChartGeometry
{
    /// <summary>
    /// A line through every point that bends smoothly between them without overshooting any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ordinary smooth curve through a day of 0, then 900, then 0 swings below zero on either side
    /// of the peak - a chart of takings that dips into negative money. This one is monotone between
    /// neighbours (the Fritsch-Carlson construction): it never goes higher or lower than the two
    /// points it is travelling between, so a flat week stays flat and nothing dips under the axis.
    /// </para>
    /// <para>
    /// With <paramref name="baseline"/> the shape is closed down to it, for the filled area under a line.
    /// </para>
    /// </remarks>
    public static StreamGeometry SmoothLine(IReadOnlyList<Point> points, double? baseline = null)
    {
        var geometry = new StreamGeometry();

        if (points.Count == 0)
        {
            geometry.Freeze();
            return geometry;
        }

        using (var context = geometry.Open())
        {
            if (baseline is { } floor)
            {
                context.BeginFigure(new Point(points[0].X, floor), isFilled: true, isClosed: true);
                context.LineTo(points[0], isStroked: true, isSmoothJoin: true);
            }
            else
            {
                context.BeginFigure(points[0], isFilled: false, isClosed: false);
            }

            if (points.Count == 2)
            {
                context.LineTo(points[1], true, true);
            }
            else if (points.Count > 2)
            {
                var slopes = Tangents(points);

                for (var i = 0; i < points.Count - 1; i++)
                {
                    var from = points[i];
                    var to = points[i + 1];
                    var third = (to.X - from.X) / 3;

                    context.BezierTo(
                        new Point(from.X + third, from.Y + (slopes[i] * third)),
                        new Point(to.X - third, to.Y - (slopes[i + 1] * third)),
                        to,
                        isStroked: true,
                        isSmoothJoin: true);
                }
            }

            if (baseline is { } bottom)
                context.LineTo(new Point(points[^1].X, bottom), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The slope the curve leaves each point at, limited so it cannot overshoot.</summary>
    internal static double[] Tangents(IReadOnlyList<Point> points)
    {
        var n = points.Count;
        var secants = new double[n - 1];
        var slopes = new double[n];

        for (var i = 0; i < n - 1; i++)
        {
            var dx = points[i + 1].X - points[i].X;
            secants[i] = dx == 0 ? 0 : (points[i + 1].Y - points[i].Y) / dx;
        }

        slopes[0] = secants[0];
        slopes[n - 1] = secants[n - 2];

        for (var i = 1; i < n - 1; i++)
            slopes[i] = secants[i - 1] * secants[i] <= 0 ? 0 : (secants[i - 1] + secants[i]) / 2;

        for (var i = 0; i < n - 1; i++)
        {
            if (secants[i] == 0)
            {
                slopes[i] = 0;
                slopes[i + 1] = 0;
                continue;
            }

            var a = slopes[i] / secants[i];
            var b = slopes[i + 1] / secants[i];
            var length = (a * a) + (b * b);

            if (length > 9)
            {
                var scale = 3 / Math.Sqrt(length);
                slopes[i] = scale * a * secants[i];
                slopes[i + 1] = scale * b * secants[i];
            }
        }

        return slopes;
    }

    /// <summary>A bar with its top corners rounded and its foot square on the axis.</summary>
    public static StreamGeometry Bar(Rect bar, double radius, bool upward = true)
    {
        var geometry = new StreamGeometry();
        radius = Math.Max(0, Math.Min(radius, Math.Min(bar.Width / 2, bar.Height)));

        using (var context = geometry.Open())
        {
            if (upward)
            {
                context.BeginFigure(bar.BottomLeft, isFilled: true, isClosed: true);
                context.LineTo(new Point(bar.Left, bar.Top + radius), true, false);
                context.ArcTo(new Point(bar.Left + radius, bar.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(bar.Right - radius, bar.Top), true, false);
                context.ArcTo(new Point(bar.Right, bar.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(bar.BottomRight, true, false);
            }
            else
            {
                // Below the axis the rounded end is at the bottom.
                context.BeginFigure(bar.TopLeft, isFilled: true, isClosed: true);
                context.LineTo(bar.TopRight, true, false);
                context.LineTo(new Point(bar.Right, bar.Bottom - radius), true, false);
                context.ArcTo(new Point(bar.Right - radius, bar.Bottom), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                context.LineTo(new Point(bar.Left + radius, bar.Bottom), true, false);
                context.ArcTo(new Point(bar.Left, bar.Bottom - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A slice of a ring, clockwise from <paramref name="startDegrees"/> (0 is twelve o'clock).</summary>
    public static StreamGeometry RingSlice(Point centre, double outer, double inner, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        sweepDegrees = Math.Min(sweepDegrees, 359.99);

        if (sweepDegrees <= 0)
        {
            geometry.Freeze();
            return geometry;
        }

        var large = sweepDegrees > 180;
        var endDegrees = startDegrees + sweepDegrees;

        using (var context = geometry.Open())
        {
            context.BeginFigure(OnCircle(centre, outer, startDegrees), isFilled: true, isClosed: true);
            context.ArcTo(OnCircle(centre, outer, endDegrees), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            context.LineTo(OnCircle(centre, inner, endDegrees), true, false);
            context.ArcTo(OnCircle(centre, inner, startDegrees), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The point <paramref name="radius"/> out from the centre at a clock angle in degrees.</summary>
    public static Point OnCircle(Point centre, double radius, double degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180;
        return new Point(centre.X + (radius * Math.Cos(radians)), centre.Y + (radius * Math.Sin(radians)));
    }
}
