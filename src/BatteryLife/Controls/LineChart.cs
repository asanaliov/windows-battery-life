using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace BatteryLife.Controls;

public readonly record struct ChartPoint(DateTime Date, double Value);

/// <summary>
/// A small time-series line chart with a shaded area, date axis and hover readout.
/// Colors are plain brush properties, so theme changes redraw it through DynamicResource.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double LabelSize = 12;
    private const double TopPadding = 8;
    private const double RightPadding = 4;
    private const double BottomPadding = 24;
    private const double AxisGap = 8;
    private const double MinimumDateLabelSpacing = 90;

    public static readonly DependencyProperty PointsProperty = Register<IReadOnlyList<ChartPoint>?>(nameof(Points), null);
    public static readonly DependencyProperty MinimumValueProperty = Register(nameof(MinimumValue), double.NaN);
    public static readonly DependencyProperty MaximumValueProperty = Register(nameof(MaximumValue), double.NaN);
    public static readonly DependencyProperty ValueFormatProperty = Register(nameof(ValueFormat), "0");
    public static readonly DependencyProperty LineBrushProperty = Register<Brush>(nameof(LineBrush), Brushes.SteelBlue);
    public static readonly DependencyProperty LabelBrushProperty = Register<Brush>(nameof(LabelBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush>(nameof(GridBrush), Brushes.LightGray);
    public static readonly DependencyProperty ReadoutBackgroundProperty = Register<Brush>(nameof(ReadoutBackground), Brushes.White);
    public static readonly DependencyProperty ReadoutBorderProperty = Register<Brush>(nameof(ReadoutBorder), Brushes.LightGray);
    public static readonly DependencyProperty ReadoutForegroundProperty = Register<Brush>(nameof(ReadoutForeground), Brushes.Black);

    private Rect _plot;
    private int? _hoverIndex;

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Fixes the bottom of the value axis. NaN fits it to the data.</summary>
    public double MinimumValue
    {
        get => (double)GetValue(MinimumValueProperty);
        set => SetValue(MinimumValueProperty, value);
    }

    /// <summary>Fixes the top of the value axis. NaN fits it to the data.</summary>
    public double MaximumValue
    {
        get => (double)GetValue(MaximumValueProperty);
        set => SetValue(MaximumValueProperty, value);
    }

    /// <summary>A .NET number format for axis labels and the hover readout, such as "0.#'%'".</summary>
    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush ReadoutBackground
    {
        get => (Brush)GetValue(ReadoutBackgroundProperty);
        set => SetValue(ReadoutBackgroundProperty, value);
    }

    public Brush ReadoutBorder
    {
        get => (Brush)GetValue(ReadoutBorderProperty);
        set => SetValue(ReadoutBorderProperty, value);
    }

    public Brush ReadoutForeground
    {
        get => (Brush)GetValue(ReadoutForegroundProperty);
        set => SetValue(ReadoutForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // A transparent backdrop makes the whole chart area respond to the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var points = Points;
        if (points is null || points.Count < 2)
        {
            return;
        }

        var (low, high, step) = GetValueScale(points);
        var tickLabels = new List<(double Value, FormattedText Text)>();
        for (var value = low; value <= high + step / 1000; value += step)
        {
            tickLabels.Add((value, CreateText(value.ToString(ValueFormat), LabelBrush)));
        }

        var left = tickLabels.Max(t => t.Text.Width) + AxisGap;
        _plot = new Rect(
            left,
            TopPadding,
            Math.Max(0, ActualWidth - left - RightPadding),
            Math.Max(0, ActualHeight - TopPadding - BottomPadding));
        if (_plot.Width <= 0 || _plot.Height <= 0)
        {
            return;
        }

        var firstDate = points.Min(p => p.Date);
        var lastDate = points.Max(p => p.Date);
        double X(DateTime date) => lastDate > firstDate
            ? _plot.Left + (date - firstDate) / (lastDate - firstDate) * _plot.Width
            : _plot.Left;
        double Y(double value) => _plot.Bottom - (value - low) / (high - low) * _plot.Height;

        var gridPen = new Pen(GridBrush, 1);
        foreach (var (value, text) in tickLabels)
        {
            var y = Y(value);
            dc.PushGuidelineSet(new GuidelineSet([], [y]));
            dc.DrawLine(gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.Pop();
            dc.DrawText(text, new Point(left - AxisGap - text.Width, y - text.Height / 2));
        }

        DrawDateLabels(dc, firstDate, lastDate);

        var line = new PointCollection(points.Select(p => new Point(X(p.Date), Y(p.Value))));
        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(line[0].X, _plot.Bottom), isFilled: true, isClosed: true);
            context.PolyLineTo(line, isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(line[^1].X, _plot.Bottom), isStroked: false, isSmoothJoin: false);
        }
        area.Freeze();

        var fill = LineBrush.Clone();
        fill.Opacity = 0.14;
        dc.DrawGeometry(fill, null, area);

        var stroke = new StreamGeometry();
        using (var context = stroke.Open())
        {
            context.BeginFigure(line[0], isFilled: false, isClosed: false);
            context.PolyLineTo(line.Skip(1).ToArray(), isStroked: true, isSmoothJoin: true);
        }
        stroke.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round }, stroke);

        if (_hoverIndex is { } index && index < points.Count)
        {
            DrawReadout(dc, points[index], line[index]);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var points = Points;
        if (points is null || points.Count < 2 || _plot.Width <= 0)
        {
            return;
        }

        // Points are in date order, so the position along the axis picks the nearest one.
        var firstDate = points.Min(p => p.Date);
        var lastDate = points.Max(p => p.Date);
        var fraction = Math.Clamp((e.GetPosition(this).X - _plot.Left) / _plot.Width, 0, 1);
        var target = firstDate + (lastDate - firstDate) * fraction;

        var nearest = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if ((points[i].Date - target).Duration() < (points[nearest].Date - target).Duration())
            {
                nearest = i;
            }
        }

        if (_hoverIndex != nearest)
        {
            _hoverIndex = nearest;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = null;
        InvalidateVisual();
    }

    private void DrawDateLabels(DrawingContext dc, DateTime firstDate, DateTime lastDate)
    {
        var count = Math.Max(2, (int)(_plot.Width / MinimumDateLabelSpacing));
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            var fraction = (double)i / (count - 1);
            var label = (firstDate + (lastDate - firstDate) * fraction).ToString("MMM d", CultureInfo.CurrentCulture);
            if (label == previous)
            {
                continue;
            }
            previous = label;

            // The end labels stay inside the plot rather than being centered on its edges.
            var text = CreateText(label, LabelBrush);
            var x = _plot.Left + _plot.Width * fraction - text.Width * fraction;
            dc.DrawText(text, new Point(x, _plot.Bottom + 6));
        }
    }

    private void DrawReadout(DrawingContext dc, ChartPoint point, Point position)
    {
        dc.DrawEllipse(ReadoutBackground, new Pen(LineBrush, 2), position, 4, 4);

        var date = CreateText(point.Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), LabelBrush);
        var value = CreateText(point.Value.ToString(ValueFormat), ReadoutForeground);
        value.SetFontWeight(FontWeights.SemiBold);

        const double padding = 8;
        const double gap = 12;
        var width = Math.Max(date.Width, value.Width) + padding * 2;
        var height = date.Height + value.Height + 2 + padding * 2;

        var x = position.X + gap + width <= ActualWidth ? position.X + gap : position.X - gap - width;
        var y = Math.Clamp(position.Y - height / 2, 0, Math.Max(0, ActualHeight - height));
        var box = new Rect(Math.Max(0, x), y, width, height);

        dc.DrawRoundedRectangle(ReadoutBackground, new Pen(ReadoutBorder, 1), box, 4, 4);
        dc.DrawText(date, new Point(box.Left + padding, box.Top + padding));
        dc.DrawText(value, new Point(box.Left + padding, box.Top + padding + date.Height + 2));
    }

    private (double Low, double High, double Step) GetValueScale(IReadOnlyList<ChartPoint> points)
    {
        var dataLow = double.IsNaN(MinimumValue) ? points.Min(p => p.Value) : MinimumValue;
        var dataHigh = double.IsNaN(MaximumValue) ? points.Max(p => p.Value) : MaximumValue;
        var step = GetTickStep(Math.Max(dataHigh - dataLow, 1) / 4);

        var low = Math.Floor(dataLow / step) * step;
        var high = Math.Ceiling(dataHigh / step) * step;
        return (low, high > low ? high : low + step, step);
    }

    /// <summary>Rounds up to a readable interval: 1, 2 or 5 times a power of ten, and never below 1.</summary>
    private static double GetTickStep(double roughStep)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return Math.Max(1, nice * magnitude);
    }

    private FormattedText CreateText(string text, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextElement.GetFontFamily(this),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            LabelSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(T),
            typeof(LineChart),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
