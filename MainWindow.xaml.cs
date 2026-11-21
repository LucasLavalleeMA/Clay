using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Clay.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Clay.UI;

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
    private static readonly string DataDirectory = AppInfo.DataDirectory;
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
    private bool _alwaysOnTop;
    private bool _draggingSplitter;
    private double _splitterStartX;
    private double _splitterStartEditorWidth;

    public MainWindow()
    {
        InitializeComponent();

        Title = $"Clay {AppInfo.DisplayVersion}";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Icon", "icon.ico"));
        AppWindow.Resize(new SizeInt32(1040, 680));
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); AutoSave(); };
        Closed += (_, _) => AutoSave();
        SizeChanged += (_, _) => UpdatePinButtonInset();
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
        UpdatePinButtonInset();
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
            AutomationProperties.SetAutomationId(cell, $"Result{i}");

            cell.Height = cell.LineHeight = _lineHeight;
            cell.ClearValue(TextBlock.ForegroundProperty);

            switch (r.Kind)
            {
                case LineKind.Result:
                    cell.Text = r.Text;
                    cell.Tag = r.Text;
                    cell.Style = (Style)LayoutRoot.Resources["ResultText"];
                    if (_stickyTheme) cell.Foreground = _stickyResultBrush;
                    ToolTipService.SetToolTip(cell, r.Text);
                    break;
                case LineKind.Error:
                    cell.Text = r.Text;
                    cell.Tag = null;
                    cell.Style = (Style)LayoutRoot.Resources["ErrorText"];
                    ToolTipService.SetToolTip(cell, r.Text);
                    break;
                default:
                    cell.Text = "";
                    cell.Tag = null;
                    ToolTipService.SetToolTip(cell, null);
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
                _currentFile = file;
                _calc.BaseDirectory = Path.GetDirectoryName(file.Path);
                Editor.Text = text;
                Recalculate();
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
                var newBaseDir = Path.GetDirectoryName(file.Path);
                bool baseDirChanged = !string.Equals(_calc.BaseDirectory, newBaseDir, StringComparison.OrdinalIgnoreCase);
                _calc.BaseDirectory = newBaseDir;
                if (baseDirChanged)
                    Recalculate();
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
        UpdateStatus();
        SaveSettings();
    }

    private void OnDegrees(object sender, RoutedEventArgs e)
    {
        _calc.AngleMode = AngleMode.Degrees;
        Recalculate();
        UpdateStatus();
        SaveSettings();
    }

    private void OnIntervalMode(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } && int.TryParse(tag, out int mode))
        {
            _calc.IntervalMode = mode;
            Recalculate();
            UpdateStatus();
            SaveSettings();
        }
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
        UpdateStatus();
        SaveSettings();
    }

    private void OnIgnoreWhitespace(object sender, RoutedEventArgs e)
    {
        _calc.IgnoreWhitespace = IgnoreWhitespaceToggle.IsChecked;
        Recalculate();
        SaveSettings();
    }

    private void OnAssumePositive(object sender, RoutedEventArgs e)
    {
        _calc.AssumePositive = AssumePositiveToggle.IsChecked;
        Recalculate();
        UpdateStatus();
        SaveSettings();
    }

    private void OnPinToggle(object sender, RoutedEventArgs e)
    {
        ApplyAlwaysOnTop(PinButton.IsChecked == true);
        UpdateStatus();
        SaveSettings();
    }

    private void ApplyAlwaysOnTop(bool pinned)
    {
        _alwaysOnTop = pinned;
        PinButton.IsChecked = pinned;
        PinIcon.Glyph = pinned ? "\uE840" : "\uE718";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = pinned;
    }

    private void UpdatePinButtonInset()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        double scale = LayoutRoot.XamlRoot?.RasterizationScale ?? 1.0;
        double rightInset = scale > 0 ? AppWindow.TitleBar.RightInset / scale : AppWindow.TitleBar.RightInset;
        PinButton.Margin = new Thickness(0, 0, Math.Max(0, rightInset), 0);
    }

    private void ResizeEditorPane(double editorWidth)
    {
        double total = ContentSurface.ActualWidth - PaneSplitter.ActualWidth;
        if (total <= 320) return;
        double clamped = Math.Clamp(editorWidth, 180, total - 140);
        EditorColumn.Width = new GridLength(clamped, GridUnitType.Star);
        ResultsColumn.Width = new GridLength(total - clamped, GridUnitType.Star);
    }

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ContentSurface.ActualWidth <= 0) return;
        _draggingSplitter = true;
        _splitterStartX = e.GetCurrentPoint(ContentSurface).Position.X;
        _splitterStartEditorWidth = EditorColumn.ActualWidth;
        PaneSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        double delta = e.GetCurrentPoint(ContentSurface).Position.X - _splitterStartX;
        ResizeEditorPane(_splitterStartEditorWidth + delta);
        e.Handled = true;
    }

    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        const double step = 24;
        switch (e.Key)
        {
            case VirtualKey.Left:
                ResizeEditorPane(EditorColumn.ActualWidth - step);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                ResizeEditorPane(EditorColumn.ActualWidth + step);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                EditorColumn.Width = new GridLength(62, GridUnitType.Star);
                ResultsColumn.Width = new GridLength(38, GridUnitType.Star);
                e.Handled = true;
                break;
        }
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;
        _draggingSplitter = false;
        PaneSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
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
            PinButton.Foreground = _stickyTitleTextBrush;
            PinButton.Resources["ToggleButtonForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            PinButton.Resources["ToggleButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            PinButton.Resources["ToggleButtonForegroundChecked"] = _stickyTitleTextBrush;
            PinButton.Resources["ToggleButtonForegroundCheckedPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            PinButton.Resources["ToggleButtonForegroundCheckedPressed"] = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            PinButton.Resources["ToggleButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 166, 95, 31));
            PinButton.Resources["ToggleButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 107, 57, 14));
            PinButton.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(Color.FromArgb(255, 166, 95, 31));
            PinButton.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 166, 95, 31));
            PinButton.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(Color.FromArgb(255, 107, 57, 14));
        }
        else
        {
            AppTitleBar.ClearValue(Panel.BackgroundProperty);
            TitleText.ClearValue(TextBlock.ForegroundProperty);
            PinButton.ClearValue(Control.ForegroundProperty);
            PinButton.Resources.Clear();
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
        MATH            2 + 3 * 4    (2 + 3)^2    10 mod 3    5 // 2    5!
        PERCENT         20% of 150   100 + 15%    200 - 10%   10 - x% = 8
                        15% on 80    15% off 80   50 as a % of 200
        VARIABLES       rent = 1200  or  a := 5   delete a
        LABELS          Groceries: 45 + 12
        SCALES          5k   2.5M   3 million   1.2 billion
        UNITS           10 km in miles    72 °F in °C    5 ft + 12 in
                        60 mph in km/h    1000 N to base    1 GiB to b?byte
        CURRENCY        $100 in EUR   50 GBP to JPY
        BASES & BITS    255 in hex   0b1010 & 0b1100 to bin8   42 to base 16
                        2024 to roman   1.5 to sexa   hex(FF)   float(...)
        STRINGS & DATES len("hello")   code("A")   65 to unicode
                        "2024-01-15" + 10 d   addDays("2024-01-15"; 5)
                        "2024-01-15T12:00Z" to utc-5   10h 30min to time
        GEOMETRY        rect(3, 4)   circle(5)   hypot(3, 4)   sphere(2)
        STATISTICS      mean stdev variance median mode quartile percentile
                        quadraticfit([5 3 4 5 6 7 13 24])   normdist ttest
                        load("data.csv")  (relative to open/saved file)
        MATRICES        [1 2; 3 4]^2   [1 2; 3 4]^-1   det   rref   rk   .'
                        dot([1 2], [3 4])   cross([1 0 0], [0 1 0])   norm
        COMPLEX & ERR   (2 + 3i) * (1 - i)   10 +/- 0.5   Ei(2)   lambertw(1)
        ALGEBRA         x^2 - 3x + 1 = 0   sin(x) + cos(x) = 1   5^x = 3
                        factor x + 2*sqrt(x) + 1   coeff degree primpart
                        newtonsolve(Ei(x) = 3, 1)   secantsolve(Ei(x) = 3, 1, 4)
        CALCULUS        diff(6x^2)   integrate(6x^2; 1; 5)   romberg(5x, 1, 5)
                        limit(sin(x)/x, 0)   fresnels(5)   gammainc   betainc
        AGGREGATES      sum  total  average  prev
                        (lines above, back to a blank line or heading)
                        +5  *2  /4  ^2 at the start of a line use prev
                        (-5 is just negative 5)
        COMMENTS        # Heading      // full-line note      5 + 3 # inline
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
            _calc.IgnoreWhitespace = settings.IgnoreWhitespace;
            _calc.AssumePositive = settings.AssumePositive;
            _calc.IntervalMode = settings.IntervalMode;
            Decimals2.IsChecked = settings.Decimals == 2;
            Decimals4.IsChecked = settings.Decimals == 4;
            Decimals6.IsChecked = settings.Decimals == 6;
            Decimals8.IsChecked = settings.Decimals == 8;
            Decimals10.IsChecked = settings.Decimals == 10;
            Decimals12.IsChecked = settings.Decimals == 12;
            RadiansOption.IsChecked = _calc.AngleMode == AngleMode.Radians;
            DegreesOption.IsChecked = _calc.AngleMode == AngleMode.Degrees;
            IntervalModeGaussian.IsChecked = settings.IntervalMode == 1;
            IntervalModeInterval.IsChecked = settings.IntervalMode == 2;
            GroupToggle.IsChecked = settings.GroupDigits;
            PhysicsToggle.IsChecked = settings.PhysicsUnits;
            IgnoreWhitespaceToggle.IsChecked = settings.IgnoreWhitespace;
            AssumePositiveToggle.IsChecked = settings.AssumePositive;
            DefaultTheme.IsChecked = settings.Theme == "Default";
            LightTheme.IsChecked = settings.Theme == "Light";
            DarkTheme.IsChecked = settings.Theme == "Dark";
            StickyTheme.IsChecked = settings.Theme == "Sticky";
            ApplyTheme(settings.Theme);
            ApplyAlwaysOnTop(settings.AlwaysOnTop);
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
            var settings = new Settings(
                _calc.Decimals,
                _calc.AngleMode.ToString(),
                _calc.GroupDigits,
                _theme,
                _calc.PhysicsUnits,
                _calc.IgnoreWhitespace,
                _alwaysOnTop,
                _calc.AssumePositive,
                _calc.IntervalMode);
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
        var modes = new List<string>();
        if (_calc.AngleMode == AngleMode.Degrees) modes.Add("Deg");
        if (_calc.PhysicsUnits) modes.Add("Physics");
        if (_calc.AssumePositive) modes.Add("Positive");
        if (_calc.IntervalMode == 2) modes.Add("Interval");
        if (_alwaysOnTop) modes.Add("Pinned");

        var rate = string.IsNullOrEmpty(_rateError) ? _rateStatus : $"{_rateStatus} (refresh failed: {_rateError})";
        var parts = new List<string>();
        if (modes.Count > 0) parts.Add(string.Join(" · ", modes));
        parts.Add(rate);
        if (!string.IsNullOrEmpty(_activityStatus)) parts.Add(_activityStatus);
        StatusText.Text = string.Join(" | ", parts);
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
