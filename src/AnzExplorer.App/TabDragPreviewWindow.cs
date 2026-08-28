using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace AnzExplorer.App;

internal enum TabDragPreviewState
{
    Reorder,
    Merge,
    TearOut
}

internal sealed class TabDragPreviewWindow : Window
{
    private const int PreviewWidth = 276;
    private const int PreviewHeight = 62;
    private readonly Border _frame;
    private readonly TextBlock _actionText;
    private bool _shown;

    public TabDragPreviewWindow(string title)
    {
        _actionText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 186, 199, 216)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 243, 247, 252)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(_actionText);

        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon
        {
            Glyph = "\uE8B7",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 19,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 29, 38, 50)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 96, 165, 250)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 8, 12, 8),
            Child = layout
        };
        Content = _frame;

        AppWindow.IsShownInSwitchers = false;
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.Resize(new SizeInt32(PreviewWidth, PreviewHeight));
    }

    public void ShowAt(PointInt32 pointer, string action, TabDragPreviewState state)
    {
        _actionText.Text = action;
        var accent = state switch
        {
            TabDragPreviewState.Merge => Color.FromArgb(255, 52, 211, 153),
            TabDragPreviewState.TearOut => Color.FromArgb(255, 251, 191, 36),
            _ => Color.FromArgb(255, 96, 165, 250)
        };
        _frame.BorderBrush = new SolidColorBrush(accent);

        var displayArea = DisplayArea.GetFromPoint(pointer, DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var x = Math.Clamp(pointer.X + 18, workArea.X, workArea.X + workArea.Width - PreviewWidth);
        var y = Math.Clamp(pointer.Y + 22, workArea.Y, workArea.Y + workArea.Height - PreviewHeight);
        AppWindow.Move(new PointInt32(x, y));
        if (_shown) return;

        AppWindow.Show(activateWindow: false);
        _shown = true;
    }
}
