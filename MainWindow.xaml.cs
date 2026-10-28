using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Clay.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Clay;

public sealed partial class MainWindow : Window
{
    private const string Sample = """
        # Welcome to Clay
        Type anything. Results appear on the right.

        # Budget
        rent = 1200
        utilities = 180 + 65
        Groceries: 340 + 20%
        Total

        # Conversions
        10 km in miles
        72 °F in °C
        $100 in EUR
        255 in hex

        # Percentages
        20% of 150
        what is 15% of $80
        50 as a % of 200
        """;

    private readonly Calculator _calc = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly string _autosavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clay", "document.txt");

    private List<LineResult> _results = [];
    private ScrollViewer? _editorScroll;
    private double _lineHeight = 24;
    private readonly Brush _stickySurfaceBrush = new SolidColorBrush(Color.FromArgb(255, 255, 249, 183));
    private readonly Brush _stickyTextBrush = new SolidColorBrush(Color.FromArgb(255, 55, 48, 20));
    private readonly Brush _stickyResultBrush = new SolidColorBrush(Color.FromArgb(255, 117, 83, 0));
    private Brush? _defaultSurfaceBrush;
    private Brush? _defaultEditorForeground;
    private bool _stickyTheme;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Clay";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Icon", "icon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 680));
        _defaultSurfaceBrush = ContentSurface.Background;
        _defaultEditorForeground = Editor.Foreground;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); AutoSave(); };
        Closed += (_, _) => AutoSave();
        LayoutRoot.Loaded += OnLoaded;
        ResultsPanel.SizeChanged += (_, _) => SyncScroll();

        Editor.Text = LoadAutosave() ?? Sample.Replace("\r\n", "\n");
    }

    // ---------------------------------------------------------------- startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureLineHeight();
        EnsureEditorScroll();
        Recalculate();
        Editor.Focus(FocusState.Programmatic);
        Editor.Select(Editor.Text.Length, 0);

        //StatusText.Text = "Using built-in exchange rates";
        _ = RefreshRatesAsync();
    }

    /// <summary>The results column lines up with the editor because both use one fixed line height.</summary>
    private void MeasureLineHeight()
    {
        var probe = new TextBlock { Text = "Xg", FontFamily = Editor.FontFamily, FontSize = Editor.FontSize };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (probe.DesiredSize.Height > 0) _lineHeight = probe.DesiredSize.Height;
    }

    private void EnsureEditorScroll()
    {
        if (_editorScroll != null) return;

        Editor.ApplyTemplate();
        _editorScroll = FindDescendant<ScrollViewer>(Editor);
        if (_editorScroll != null)
            _editorScroll.ViewChanged += (_, _) => SyncScroll();
    }

    private static TElement? FindDescendant<TElement>(DependencyObject root) where TElement : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TElement match) return match;
            var nested = FindDescendant<TElement>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    // ------------------------------------------------------------ calculation

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Recalculate();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Recalculate()
    {
        _results = _calc.Evaluate(Editor.Text);
        RenderResults();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EnsureEditorScroll();
            SyncScroll();
        });
    }

    private void RenderResults()
    {
        var cells = ResultsPanel.Children;
        while (cells.Count < _results.Count) cells.Add(MakeCell());
        while (cells.Count > _results.Count) cells.RemoveAt(cells.Count - 1);

        for (int i = 0; i < _results.Count; i++)
        {
            var cell = (TextBlock)cells[i];
            var r = _results[i];

            cell.Height = cell.LineHeight = _lineHeight;
            cell.ClearValue(TextBlock.ForegroundProperty);

            switch (r.Kind)
            {
                case LineKind.Result:
                    cell.Text = r.Text;
                    cell.Tag = r.Text;
                    cell.Style = (Style)LayoutRoot.Resources["ResultText"];
                    if (_stickyTheme) cell.Foreground = _stickyResultBrush;
                    break;
                case LineKind.Error:
                    cell.Text = r.Text;
                    cell.Tag = null;
                    cell.Style = (Style)LayoutRoot.Resources["ErrorText"];
                    break;
                default:
                    cell.Text = "";
                    cell.Tag = null;
                    break;
            }
        }
    }

    private TextBlock MakeCell()
    {
        var cell = new TextBlock
        {
            FontFamily = Editor.FontFamily,
            FontSize = Editor.FontSize,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        cell.Tapped += OnResultTapped;
        return cell;
    }

    private void SyncScroll()
    {
        if (_editorScroll != null)
            ResultsScroll.ChangeView(null, _editorScroll.VerticalOffset, null, disableAnimation: true);
    }

    private void OnResultsWheel(object sender, PointerRoutedEventArgs e)
    {
        // The results column never scrolls on its own; forward the wheel to the editor.
        if (_editorScroll != null)
        {
            int delta = e.GetCurrentPoint(ResultsScroll).Properties.MouseWheelDelta;
            _editorScroll.ChangeView(null, _editorScroll.VerticalOffset - delta, null, disableAnimation: true);
        }
        e.Handled = true;
    }

    private void OnResultTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is TextBlock { Tag: string text })
        {
            CopyToClipboard(text);
            //StatusText.Text = $"Copied {text}";
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // --------------------------------------------------------------- commands

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".Clay");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        Editor.Text = await FileIO.ReadTextAsync(file);
        //StatusText.Text = $"Opened {file.Name}";
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "Untitled"
        };
        picker.FileTypeChoices.Add("Text document", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("Clay document", new List<string> { ".Clay" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        await FileIO.WriteTextAsync(file, ToWindowsNewlines(Editor.Text));
        //StatusText.Text = $"Saved {file.Name}";
    }

    private void OnCopyResults(object sender, RoutedEventArgs e)
    {
        var lines = Regex.Split(Editor.Text, "\r\n|\r|\n");
        var rows = new List<string>();
        for (int i = 0; i < _results.Count && i < lines.Length; i++)
        {
            if (_results[i].Kind == LineKind.Result)
                rows.Add($"{lines[i].Trim()} = {_results[i].Text}");
        }

        CopyToClipboard(string.Join(Environment.NewLine, rows));
        //StatusText.Text = $"Copied {rows.Count} result{(rows.Count == 1 ? "" : "s")}";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Editor.SelectAll();
        Editor.SelectedText = "";   // keeps undo history, unlike assigning Text
        Editor.Focus(FocusState.Programmatic);
    }

    private void OnDecimals(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } && int.TryParse(tag, out int d))
        {
            _calc.Decimals = d;
            Recalculate();
        }
    }

    private void OnRadians(object sender, RoutedEventArgs e)
    {
        _calc.AngleMode = AngleMode.Radians;
        Recalculate();
    }

    private void OnDegrees(object sender, RoutedEventArgs e)
    {
        _calc.AngleMode = AngleMode.Degrees;
        Recalculate();
    }

    private void OnGroupDigits(object sender, RoutedEventArgs e)
    {
        _calc.GroupDigits = GroupToggle.IsChecked;
        Recalculate();
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            _stickyTheme = tag.Equals("Sticky", StringComparison.OrdinalIgnoreCase);
            LayoutRoot.RequestedTheme = _stickyTheme ? ElementTheme.Light : Enum.Parse<ElementTheme>(tag);

            if (_stickyTheme)
            {
                ContentSurface.Background = _stickySurfaceBrush;
                Editor.Foreground = _stickyTextBrush;
            }
            else
            {
                ContentSurface.Background = _defaultSurfaceBrush;
                Editor.Foreground = _defaultEditorForeground;
            }

            RenderResults();
        }
    }

    private async void OnRefreshRates(object sender, RoutedEventArgs e) => await RefreshRatesAsync();

    private async void OnHelp(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Clay cheat sheet",
            CloseButtonText = "Close",
            XamlRoot = LayoutRoot.XamlRoot,
            Content = new ScrollViewer
            {
                MaxHeight = 440,
                Content = new TextBlock
                {
                    Text = HelpText,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 13,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                }
            }
        };
        await dialog.ShowAsync();
    }

    private const string HelpText = """
        MATH            2 + 3 * 4    (2 + 3)^2    10 mod 3    5!
        PERCENT         20% of 150   100 + 15%   200 - 10%
                        15% on 80    15% off 80  50 as a % of 200
        VARIABLES       rent = 1200   then   rent * 12
        LABELS          Groceries: 45 + 12
        SCALES          5k   2.5M   3 million   1.2 billion
        UNITS           10 km in miles    72 °F in °C
                        5 ft + 12 in      60 mph in km/h
        CURRENCY        $100 in EUR   50 GBP to JPY
        BASES           255 in hex   0b1010 + 1   42 in bin
        FUNCTIONS       sqrt round(x, 2) abs ln log log2 exp
                        sin cos tan asin acos atan min max pow
                        sum(a, b) avg(a, b) fact
        ANGLES          sin(30 deg)    asin(0.5) in deg
        AGGREGATES      sum  total  average  prev
                        (lines above, back to a blank line or heading)
        COMMENTS        # Heading      // note
        WORDS           Unknown words are ignored:  what is 20% of 150
        """;

    // ------------------------------------------------------------ persistence

    private static string ToWindowsNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private string? LoadAutosave()
    {
        try { return File.Exists(_autosavePath) ? File.ReadAllText(_autosavePath) : null; }
        catch { return null; }
    }

    private void AutoSave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_autosavePath)!);
            File.WriteAllText(_autosavePath, ToWindowsNewlines(Editor.Text));
        }
        catch { /* autosave is best-effort */ }
    }

    // ---------------------------------------------------------- exchange rates

    private async Task RefreshRatesAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync("https://open.er-api.com/v6/latest/USD");

            using var doc = JsonDocument.Parse(json);
            var rates = new Dictionary<string, double>();
            foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject())
                rates[p.Name] = p.Value.GetDouble();

            int updated = UnitTable.UpdateRates(rates);
            //StatusText.Text = $"Exchange rates updated {DateTime.Now:t} ({updated} currencies)";
            Recalculate();
        }
        catch
        {
            //StatusText.Text = "Offline: using built-in exchange rates (approximate)";
        }
    }
}
