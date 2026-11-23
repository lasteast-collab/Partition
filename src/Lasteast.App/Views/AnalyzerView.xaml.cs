using System.Windows;
using System.Windows.Controls;
using Lasteast.Presentation.ViewModels;

namespace Lasteast.App.Views;

public partial class AnalyzerView : UserControl
{
    public AnalyzerView() => InitializeComponent();

    private void TreemapHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is StorageAnalyzerViewModel vm) vm.SetViewport(e.NewSize.Width, e.NewSize.Height);
    }
}
