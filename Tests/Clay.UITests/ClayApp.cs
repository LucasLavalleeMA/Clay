using System.Diagnostics;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace Clay.UITests;

/// <summary>Launches Clay.exe against a throwaway data directory and exposes its main window.</summary>
public sealed class ClayApp : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Application? _app;
    private readonly UIA3Automation _automation = new();

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "Clay.UITests", Guid.NewGuid().ToString("N"));
    public Window Window { get; }

    /// <param name="settingsJson">Optional settings.json contents to start with (PascalCase <c>Settings</c> fields).</param>
    public ClayApp(string? settingsJson = null)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (settingsJson != null)
                File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), settingsJson);
            var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
            start.Environment["CLAY_DATA_DIR"] = DataDirectory;

            _app = Application.Launch(start);
            Window = _app.GetMainWindow(_automation, Timeout)
                ?? throw new InvalidOperationException("Clay did not show its main window");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public TextBox Editor => Find("Editor").AsTextBox();

    public string StatusText => Find("StatusText").Name;

    /// <summary>Replaces the document; lines are separated with \n.</summary>
    public void SetDocument(string text) => Editor.Text = text;

    /// <summary>Waits for the result cell of a zero-based editor line to show the expected text.</summary>
    public string WaitForResult(int line, string expected)
    {
        Retry.WhileFalse(() => ResultText(line) == expected, Timeout, ignoreException: true);
        return ResultText(line) ?? "";
    }

    public string? ResultText(int line) =>
        Window.FindFirstDescendant(cf => cf.ByAutomationId($"Result{line}")) is { } cell
            ? cell.Properties.Name.ValueOrDefault ?? ""
            : null;

    private AutomationElement Find(string automationId) =>
        Retry.WhileNull(() => Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), Timeout).Result
            ?? throw new InvalidOperationException($"Element '{automationId}' not found");

    public void Dispose()
    {
        try
        {
            _app?.Close();
        }
        finally
        {
            _app?.Dispose();
            _automation.Dispose();
            try { Directory.Delete(DataDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string ExePath
    {
        get
        {
            string path = Environment.GetEnvironmentVariable("CLAY_EXE") is { Length: > 0 } overridden
                ? overridden
                : Path.Combine(AppContext.BaseDirectory,
                    typeof(ClayApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                        .Single(a => a.Key == "ClayExe").Value!);
            return File.Exists(path)
                ? Path.GetFullPath(path)
                : throw new FileNotFoundException("Clay.exe not found; build Clay.UI.csproj or set CLAY_EXE", path);
        }
    }
}
