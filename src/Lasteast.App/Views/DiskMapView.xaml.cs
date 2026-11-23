using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasteast.App.Views;

/// <summary>Interactive horizontal partition map for one disk (DataContext: DiskViewModel).</summary>
public partial class DiskMapView : UserControl
{
    public static readonly DependencyProperty SelectCommandProperty = DependencyProperty.Register(
        nameof(SelectCommand), typeof(ICommand), typeof(DiskMapView));

    public static readonly DependencyProperty MapHeightProperty = DependencyProperty.Register(
        nameof(MapHeight), typeof(double), typeof(DiskMapView), new PropertyMetadata(74.0));

    public DiskMapView() => InitializeComponent();

    public ICommand? SelectCommand
    {
        get => (ICommand?)GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public double MapHeight
    {
        get => (double)GetValue(MapHeightProperty);
        set => SetValue(MapHeightProperty, value);
    }
}
