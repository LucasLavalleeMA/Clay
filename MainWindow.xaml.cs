using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Windowing;
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
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clay");
    private static readonly string AutosavePath = Path.Combine(DataDirectory, "document.txt");
    private static readonly string SettingsPath = Path.Combine(DataDirectory, "settings.json");
    private static readonly string RatesPath = Path.Combine(DataDirectory, "rates.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _rateRefreshLock = new(1, 1);
    private readonly AsyncOperationQueue _documentOperations = new();
    private readonly AsyncOperationQueue _dialogs = new();

    private List<LineResult> _results = [];
    private StorageFile? _currentFile;
    private bool _autosaveEnabled = true;
    private string _theme = "Default";
    private string _rateStatus = "Using built-in approximate exchange rates";
    private string _rateError = "";
    private string _activityStatus = "";
    private ScrollViewer? _editorScroll;
    private double _lineHeight = 24;
    private readonly Brush _stickyTextBrush = new SolidColorBrush(Color.FromArgb(255, 55, 48, 20));
    private readonly Brush _stickyResultBrush = new SolidColorBrush(Color.FromArgb(255, 117, 83, 0));
    private readonly Brush _stickyTitleBrush = new SolidColorBrush(Color.FromArgb(255, 139, 74, 18));
    private readonly Brush _stickyTitleTextBrush = new SolidColorBrush(Color.FromArgb(255, 255, 249, 183));
    private bool _stickyTheme;

    public MainWindow()
    {
        InitializeComponent();

        Title = $"Clay {AppInfo.DisplayVersion}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Icon", "icon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 680));
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); AutoSave(); };
        Closed += (_, _) => AutoSave();
        LayoutRoot.Loaded += OnLoaded;
        ResultsPanel.SizeChanged += (_, _) => SyncScroll();

        LoadSettings();
        LoadCachedRates();
        Editor.Text = LoadAutosave() ?? Sample.Replace("\r\n", "\n");
        UpdateStatus();
    }

    // ---------------------------------------------------------------- startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureLineHeight();
        EnsureEditorScroll();
        Recalculate();
        Editor.Focus(FocusState.Programmatic);
        Editor.Select(Editor.Text.Length, 0);

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
            SetActivity($"Copied {text}");
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
        try
        {
            await _documentOperations.RunAsync(async () =>
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add(".txt");
                picker.FileTypeFilter.Add(".clay");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                var text = await FileIO.ReadTextAsync(file);
                Editor.Text = text;
                _currentFile = file;
                SetActivity($"Opened {file.Name}");
            });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not open file", ex);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await SaveAsync(saveAs: false);
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        await SaveAsync(saveAs: true);
    }

    private async Task SaveAsync(bool saveAs)
    {
        try
        {
            await _documentOperations.RunAsync(async () =>
            {
                var file = saveAs ? null : _currentFile;
                if (file == null)
                {
                    var picker = new FileSavePicker
                    {
                        SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                        SuggestedFileName = _currentFile?.DisplayName ?? "Untitled"
                    };
                    picker.FileTypeChoices.Add("Text document", new List<string> { ".txt" });
                    picker.FileTypeChoices.Add("Clay document", new List<string> { ".clay" });
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    file = await picker.PickSaveFileAsync();
                    if (file == null) return;
                }

                await FileIO.WriteTextAsync(file, ToWindowsNewlines(Editor.Text));
                _currentFile = file;
                SetActivity($"Saved {file.Name}");
            });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not save file", ex);
        }
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
        SetActivity($"Copied {rows.Count} result{(rows.Count == 1 ? "" : "s")}");
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
            SaveSettings();
        }
    }

    private void OnRadians(object sender, RoutedEventArgs e)
    {
        _calc.AngleMode = AngleMode.Radians;
        Recalculate();
        SaveSettings();
    }

    private void OnDegrees(object sender, RoutedEventArgs e)
    {
        _calc.AngleMode = AngleMode.Degrees;
        Recalculate();
        SaveSettings();
    }

    private void OnGroupDigits(object sender, RoutedEventArgs e)
    {
        _calc.GroupDigits = GroupToggle.IsChecked;
        Recalculate();
        SaveSettings();
    }

    private void OnPhysicsUnits(object sender, RoutedEventArgs e)
    {
        _calc.PhysicsUnits = PhysicsToggle.IsChecked;
        Recalculate();
        SaveSettings();
    }

    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            ApplyTheme(tag);
            RenderResults();
            SaveSettings();
        }
    }

    private void ApplyTheme(string theme)
    {
        _theme = theme;
        _stickyTheme = theme == "Sticky";
        LayoutRoot.RequestedTheme = _stickyTheme ? ElementTheme.Light : Enum.Parse<ElementTheme>(theme);
        StickyBackground.Visibility = _stickyTheme ? Visibility.Visible : Visibility.Collapsed;
        if (_stickyTheme) Editor.Foreground = _stickyTextBrush;
        else Editor.ClearValue(TextBox.ForegroundProperty);
        ApplyTitleBarTheme();
    }

    // The caption buttons are drawn by Windows, so their colors must be set explicitly to stay visible
    private void ApplyTitleBarTheme()
    {
        if (_stickyTheme)
        {
            AppTitleBar.Background = _stickyTitleBrush;
            TitleText.Foreground = _stickyTitleTextBrush;
        }
        else
        {
            AppTitleBar.ClearValue(Panel.BackgroundProperty);
            TitleText.ClearValue(TextBlock.ForegroundProperty);
        }

        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var bar = AppWindow.TitleBar;
        if (_stickyTheme)
        {
            var cream = Color.FromArgb(255, 255, 249, 183);
            bar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            bar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
            bar.ButtonForegroundColor = cream;
            bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 214, 184, 150);
            bar.ButtonHoverBackgroundColor = Color.FromArgb(255, 166, 95, 31);
            bar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(255, 107, 57, 14);
            bar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
        }
        else
        {
            bar.ButtonBackgroundColor = null;
            bar.ButtonInactiveBackgroundColor = null;
            bar.ButtonForegroundColor = null;
            bar.ButtonInactiveForegroundColor = null;
            bar.ButtonHoverBackgroundColor = null;
            bar.ButtonHoverForegroundColor = null;
            bar.ButtonPressedBackgroundColor = null;
            bar.ButtonPressedForegroundColor = null;
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
        await ShowDialogAsync(dialog);
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
        WORDS           Unknown variables are errors.
                        "what is" is a shortcut: what is 20% of 150
        """;

    // ------------------------------------------------------------ persistence

    private static string ToWindowsNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private string? LoadAutosave()
    {
        try { return AtomicFile.ReadAllText(AutosavePath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _autosaveEnabled = false;
            SetActivity($"Could not load autosave; autosave disabled: {ex.Message}");
            return null;
        }
    }

    private void AutoSave()
    {
        if (!_autosaveEnabled) return;
        try
        {
            AtomicFile.WriteAllText(AutosavePath, ToWindowsNewlines(Editor.Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetActivity($"Autosave failed: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(AtomicFile.ReadAllText(SettingsPath))
                ?? throw new InvalidDataException("Settings are empty");
            settings.Validate();

            _calc.Decimals = settings.Decimals;
            _calc.AngleMode = Enum.Parse<AngleMode>(settings.AngleMode);
            _calc.GroupDigits = settings.GroupDigits;
            _calc.PhysicsUnits = settings.PhysicsUnits;
            Decimals2.IsChecked = settings.Decimals == 2;
            Decimals4.IsChecked = settings.Decimals == 4;
            Decimals6.IsChecked = settings.Decimals == 6;
            Decimals8.IsChecked = settings.Decimals == 8;
            RadiansOption.IsChecked = _calc.AngleMode == AngleMode.Radians;
            DegreesOption.IsChecked = _calc.AngleMode == AngleMode.Degrees;
            GroupToggle.IsChecked = settings.GroupDigits;
            PhysicsToggle.IsChecked = settings.PhysicsUnits;
            DefaultTheme.IsChecked = settings.Theme == "Default";
            LightTheme.IsChecked = settings.Theme == "Light";
            DarkTheme.IsChecked = settings.Theme == "Dark";
            StickyTheme.IsChecked = settings.Theme == "Sticky";
            ApplyTheme(settings.Theme);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SetActivity($"Could not load settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new Settings(_calc.Decimals, _calc.AngleMode.ToString(), _calc.GroupDigits, _theme, _calc.PhysicsUnits);
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetActivity($"Could not save settings: {ex.Message}");
        }
    }

    private void SetActivity(string message)
    {
        _activityStatus = message;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var rate = string.IsNullOrEmpty(_rateError) ? _rateStatus : $"{_rateStatus} (refresh failed: {_rateError})";
        StatusText.Text = string.IsNullOrEmpty(_activityStatus) ? rate : $"{rate} | {_activityStatus}";
    }

    private async Task ShowErrorAsync(string title, Exception ex)
    {
        SetActivity($"{title}: {ex.Message}");
        await ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = ex.Message,
            CloseButtonText = "Close",
            XamlRoot = LayoutRoot.XamlRoot
        });
    }

    private Task ShowDialogAsync(ContentDialog dialog) =>
        _dialogs.RunAsync(async () => await dialog.ShowAsync());

    // ---------------------------------------------------------- exchange rates

    private void LoadCachedRates()
    {
        try
        {
            var cache = JsonSerializer.Deserialize<CachedRates>(AtomicFile.ReadAllText(RatesPath))
                ?? throw new InvalidDataException("Rate cache is empty");
            cache.Validate(DateTimeOffset.UtcNow);
            UnitTable.UpdateRates(cache.Rates);
            _rateStatus = $"Cached exchange rates from {cache.FetchedAt.LocalDateTime:g}";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            SetActivity($"Could not load cached exchange rates: {ex.Message}");
        }
    }

    private async Task RefreshRatesAsync()
    {
        await _rateRefreshLock.WaitAsync();
        try
        {
            var json = await Http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("result").GetString() != "success")
                throw new InvalidDataException("Exchange rate service reported an error");
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject())
                rates[p.Name] = p.Value.GetDouble();
            CachedRates.ValidateRates(rates);
            var fetchedAt = DateTimeOffset.UtcNow;
            UnitTable.UpdateRates(rates);
            _rateStatus = $"Live exchange rates updated {fetchedAt.LocalDateTime:g}";
            _rateError = "";
            Recalculate();
            try
            {
                AtomicFile.WriteAllText(RatesPath, JsonSerializer.Serialize(new CachedRates(fetchedAt, rates)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetActivity($"Could not cache exchange rates: {ex.Message}");
            }
            UpdateStatus();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException or FormatException)
        {
            _rateError = ex.Message;
            UpdateStatus();
        }
        finally
        {
            _rateRefreshLock.Release();
        }
    }
}
