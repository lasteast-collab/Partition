using System.Windows.Data;
using System.Windows.Markup;
using Lasteast.Core.Localization;

namespace Lasteast.App.Infrastructure;

/// <summary>
/// XAML: <c>Text="{ui:Tr Nav.Overview}"</c>. Produces a live binding to the localization indexer,
/// so the text updates instantly when the language changes.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
