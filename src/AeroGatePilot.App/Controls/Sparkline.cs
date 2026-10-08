using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace AeroGatePilot.App.Controls;

/// <summary>Lightweight area chart for one or two series of non-negative values.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(
        nameof(Primary), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(
        nameof(Secondary), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty PrimaryBrushProperty = DependencyProperty.Register(
        nameof(PrimaryBrush), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x00, 0xCE, 0xC9), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryBrushProperty = DependencyProperty.Register(
        nameof(SecondaryBrush), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Color.FromRgb(0x6C, 0x5C, 0xE7), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Primary
    {
        get => (IReadOnlyList<double>?)GetValue(PrimaryProperty);
        set => SetValue(PrimaryProperty, value);
    }

    public IReadOnlyList<double>? Secondary
    {
        get => (IReadOnlyList<double>?)GetValue(SecondaryProperty);
        set => SetValue(SecondaryProperty, value);
    }

    public Color PrimaryBrush
    {
        get => (Color)GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public Color SecondaryBrush
    {
        get => (Color)GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        for (var i = 1; i < 4; i++)
        {
            var y = Math.Round(height * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        var max = Math.Max(Max(Primary), Max(Secondary));
        if (max <= 0)
            max = 1;
        Draw(dc, Secondary, SecondaryBrush, max, width, height);
        Draw(dc, Primary, PrimaryBrush, max, width, height);
    }

    private static double Max(IReadOnlyList<double>? series) => series is { Count: > 0 } ? series.Max() : 0;

    private static void Draw(DrawingContext dc, IReadOnlyList<double>? series, Color color, double max, double width, double height)
    {
        if (series is not { Count: > 1 })
            return;

        var step = width / (series.Count - 1);
        var points = new Point[series.Count];
        for (var i = 0; i < series.Count; i++)
            points[i] = new Point(i * step, height - 4 - (height - 8) * Math.Clamp(series[i] / max, 0, 1));

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, height), true, true);
            ctx.PolyLineTo(points, true, true);
            ctx.LineTo(new Point(width, height), true, true);
        }
        area.Freeze();

        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round }, line);
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= chart.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += chart.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
