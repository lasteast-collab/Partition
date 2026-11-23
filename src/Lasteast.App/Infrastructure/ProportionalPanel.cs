using System.Windows;
using System.Windows.Controls;
using Lasteast.Core.Layout;

namespace Lasteast.App.Infrastructure;

/// <summary>
/// Lays children out horizontally with widths proportional to their <see cref="WeightProperty"/>
/// (partition size), while guaranteeing <see cref="MinItemWidth"/> so small partitions stay clickable.
/// The width math lives in <see cref="DiskMapLayout.ComputeWidths"/> (unit-tested).
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject o) => (double)o.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject o, double v) => o.SetValue(WeightProperty, v);

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(64.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private double[] Widths(double total)
    {
        var n = InternalChildren.Count;
        var sizes = new ulong[n];
        for (int i = 0; i < n; i++)
            sizes[i] = (ulong)Math.Max(0, GetWeight(InternalChildren[i]));
        return DiskMapLayout.ComputeWidths(sizes, Math.Max(0, total - Spacing * Math.Max(0, n - 1)), MinItemWidth);
    }

    protected override Size MeasureOverride(Size available)
    {
        var n = InternalChildren.Count;
        var width = double.IsInfinity(available.Width) ? n * (MinItemWidth + Spacing) : available.Width;
        var widths = Widths(width);
        double height = 0;
        for (int i = 0; i < n; i++)
        {
            InternalChildren[i].Measure(new Size(widths[i], available.Height));
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }
        return new Size(width, double.IsInfinity(available.Height) ? height : Math.Min(height, available.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var widths = Widths(final.Width);
        double x = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, widths[i], final.Height));
            x += widths[i] + Spacing;
        }
        return final;
    }
}
