using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using AnzExplorer.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;

namespace AnzExplorer.App;

public sealed class FileSystemItemViewModel : INotifyPropertyChanged
{
    private FolderSizeState _sizeState;
    private long? _logicalBytes;
    private string? _sizeDescription;
    private ImageSource? _shellIcon;

    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool IsDrive { get; set; }
    public DateTimeOffset Modified { get; set; }
    public string Type { get; set; } = string.Empty;
    public int RowIndex { get; set; }
    public string Glyph => IsDrive ? "\uEDA2" : IsFolder ? "\uE8B7" : "\uE7C3";
    public Visibility FolderIconVisibility => IsFolder && !IsDrive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphIconVisibility => IsFolder && !IsDrive ? Visibility.Collapsed : Visibility.Visible;
    public bool IsImageFile => !IsFolder && System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".bmp" or ".gif" or ".heic" or ".heif" or ".ico" or ".jfif" or ".jpeg" or ".jpg"
            or ".png" or ".svg" or ".tif" or ".tiff" or ".webp" => true,
        _ => false
    };
    public Brush IconBrush => (Brush)Application.Current.Resources[ResolveIconBrushKey()];
    public ImageSource? ShellIcon
    {
        get => _shellIcon;
        set
        {
            if (ReferenceEquals(_shellIcon, value)) return;
            _shellIcon = value;
            OnPropertyChanged();
        }
    }
    internal bool ShellIconLoadRequested { get; set; }
    public string ModifiedText => Modified == DateTimeOffset.MinValue ? string.Empty : Modified.LocalDateTime.ToString("g");
    public string? SizeDescription
    {
        get => _sizeDescription;
        set
        {
            if (_sizeDescription == value) return;
            _sizeDescription = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public FolderSizeState SizeState
    {
        get => _sizeState;
        set
        {
            if (_sizeState == value) return;
            _sizeState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public long? LogicalBytes
    {
        get => _logicalBytes;
        set
        {
            if (_logicalBytes == value) return;
            _logicalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText => SizeDescription ?? SizeState switch
    {
        FolderSizeState.Queued => "Queued",
        FolderSizeState.Scanning => "Calculating…",
        FolderSizeState.Unavailable => "Unavailable",
        FolderSizeState.Partial when LogicalBytes is not null => $"≥ {FormatBytes(LogicalBytes.Value)}",
        _ when LogicalBytes is not null => FormatBytes(LogicalBytes.Value),
        _ => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private string ResolveIconBrushKey()
    {
        if (IsDrive) return "AnzDriveBrush";
        if (IsFolder) return "AnzFolderBrush";

        return System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" or ".pdf" or ".rtf" or ".txt" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "AnzDocumentBrush",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => "AnzImageBrush",
            ".mp3" or ".wav" or ".flac" or ".mp4" or ".mkv" or ".mov" or ".avi" => "AnzMediaBrush",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "AnzArchiveBrush",
            ".cs" or ".cpp" or ".h" or ".js" or ".ts" or ".py" or ".json" or ".xml" or ".xaml" or ".html" or ".css" => "AnzCodeBrush",
            _ => "AnzFileBrush"
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{bytes:N0} B" : $"{value:0.##} {suffixes[suffix]}";
    }
}

public sealed class DisplayScaleState : INotifyPropertyChanged
{
    private double _scale = 1.0;

    public double Scale
    {
        get => _scale;
        set
        {
            var normalized = double.IsFinite(value) ? Math.Clamp(value, 0.8, 1.5) : 1.0;
            if (Math.Abs(_scale - normalized) < 0.001) return;
            _scale = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Scale)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ScaledDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var scale = value is double number ? number : 1.0;
        var baseline = double.TryParse(
            parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        return baseline * scale;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ColumnLayoutState : INotifyPropertyChanged
{
    private double _name = 480;
    private double _modified = 170;
    private double _type = 140;
    private double _size = 130;

    public GridLength NameWidth => new(_name);
    public GridLength ModifiedWidth => new(_modified);
    public GridLength TypeWidth => new(_type);
    public GridLength SizeWidth => new(_size);
    // Four 12 px resize targets plus 20 px of row/header horizontal padding.
    public double ContentWidth => _name + _modified + _type + _size + 68;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(DetailsColumnWidths widths)
    {
        _name = Clamp(widths.Name, 160);
        _modified = Clamp(widths.Modified, 105);
        _type = Clamp(widths.Type, 80);
        _size = Clamp(widths.Size, 75);
        NotifyAll();
    }

    public void Resize(string column, double change)
    {
        switch (column)
        {
            case "Name": _name = Clamp(_name + change, 160); break;
            case "Modified": _modified = Clamp(_modified + change, 105); break;
            case "Type": _type = Clamp(_type + change, 80); break;
            case "Size": _size = Clamp(_size + change, 75); break;
            default: return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"{column}Width"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentWidth)));
    }

    public DetailsColumnWidths ToContract() => new(_name, _modified, _type, _size);

    private static double Clamp(double value, double minimum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, 1200) : minimum;

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModifiedWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentWidth)));
    }
}

internal sealed class TabRuntime(TabSession session, MainWindow owner)
{
    public TabSession Session { get; set; } = session;
    public MainWindow Owner { get; set; } = owner;
    public ObservableCollection<FileSystemItemViewModel> Items { get; } = [];
    public string? LoadedPath { get; set; }
    public string LoadedSearch { get; set; } = string.Empty;
    public ListView? FileList { get; set; }
    public ScrollViewer? ScrollViewer { get; set; }
    public FileViewMode? AppliedViewMode { get; set; }

    // Scroll positions of folders this tab navigated away from, so Back and Up can
    // return to where the user was. In memory only; bounded so long sessions stay small.
    private readonly Dictionary<string, double> _scrollOffsets = new(StringComparer.OrdinalIgnoreCase);

    public void RememberScrollOffset(string path, double offset)
    {
        if (_scrollOffsets.Count >= 64 && !_scrollOffsets.ContainsKey(path)) _scrollOffsets.Clear();
        _scrollOffsets[path] = offset;
    }

    public double? TakeScrollOffset(string path) =>
        _scrollOffsets.Remove(path, out var offset) ? offset : null;
}
