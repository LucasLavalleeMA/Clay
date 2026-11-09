using Microsoft.UI.Xaml;

namespace Clay;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clay");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:O} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

    }
}
