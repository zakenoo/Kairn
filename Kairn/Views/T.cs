using System.Windows.Data;
using System.Windows.Markup;
using Kairn.Services;

namespace Kairn.Views;

/// <summary>
/// Texte traduit en XAML : Text="{l:T nav.today}". La valeur suit la langue en direct.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public class T : MarkupExtension
{
    public string Key { get; set; } = "";

    public T() { }
    public T(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
