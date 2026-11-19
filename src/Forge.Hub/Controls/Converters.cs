using Avalonia;
using Avalonia.Data.Converters;

namespace Forge.Hub.Controls;

public static class Converters
{
    /// <summary>A playbook's chips start after its 36 px spine.</summary>
    public static readonly IValueConverter PlaybookInset = new FuncValueConverter<bool, Thickness>(b => b ? new Thickness(28, 0, 0, 0) : default);
}
