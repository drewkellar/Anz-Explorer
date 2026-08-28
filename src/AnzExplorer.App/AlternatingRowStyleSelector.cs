using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnzExplorer.App;

public sealed class AlternatingRowStyleSelector : StyleSelector
{
    public Style? EvenStyle { get; set; }
    public Style? OddStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container) =>
        item is FileSystemItemViewModel { RowIndex: var index } && index % 2 == 0
            ? EvenStyle
            : OddStyle;
}
