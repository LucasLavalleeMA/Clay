using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Clay.Engine;

public sealed class QalcProcessEngine : IDisposable
{
    private static readonly Regex Assignment = new(@"^\s*(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*=(?!=)", RegexOptions.Compiled);
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _assignedVariables = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private int _sentinel;
    private volatile bool _disposed;

    public QalcProcessEngine(string? executablePath = null, TimeSpan? timeout = null)
    {
        _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "qalc.exe");
        _timeout = timeout ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (expression.Contains('\n') || expression.Contains('\r'))
            throw new ArgumentException("A qalc expression must be a single line.", nameof(expression));
        if (Regex.IsMatch(expression.Trim(), @"^(?:quit|exit)$", RegexOptions.IgnoreCase))
            throw new ArgumentException("The qalc process cannot be closed by an expression.", nameof(expression));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                await EnsureStartedAsync(cancellationToken);
                var result = await SendCommandAsync(expression, timeout.Token);
                if (Assignment.Match(expression) is { Success: true } assignment)
                    _assignedVariables.Add(assignment.Groups["name"].Value);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await RestartAsync();
                throw new TimeoutException($"qalc did not finish within {_timeout.TotalMilliseconds:0} ms.");
            }
            catch (OperationCanceledException)
            {
                await RestartAsync();
                throw;
            }
            catch (IOException)
            {
                await RestartAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetVariablesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            foreach (var name in _assignedVariables.ToArray())
                await SendWithTimeoutAsync($"delete {name}", cancellationToken);
            _assignedVariables.Clear();
        }
        catch (OperationCanceledException)
        {
            await RestartAsync();
            throw;
        }
        catch (IOException)
        {
            await RestartAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetVariableAsync(string name, string expression, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_]*$"))
            throw new ArgumentException("Invalid qalc variable name.", nameof(name));
        await EvaluateAsync($"{name} = {expression}", cancellationToken);
    }

    private async Task<string> SendWithTimeoutAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return await SendCommandAsync(command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RestartAsync();
            throw new TimeoutException($"qalc did not finish within {_timeout.TotalMilliseconds:0} ms.");
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;

        DisposeProcess();
        if (!File.Exists(_executablePath))
            throw new FileNotFoundException("The bundled qalc executable was not found.", _executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("-defaults");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("-set");
        startInfo.ArgumentList.Add("color 0");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => _ = RestartAfterExitAsync(process);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start the qalc process.");
        }

        _process = process;
        _ = DrainStandardErrorAsync(process);
        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SendCommandAsync("no", startupTimeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            await RestartAsync();
            throw;
        }
    }

    private async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("The qalc process is not running.");
        if (process.HasExited)
            throw new IOException(DescribeExit(process));

        string sentinel = $"__CLAY_END_{Interlocked.Increment(ref _sentinel)}__";
        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await process.StandardInput.WriteLineAsync(sentinel.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);

        var output = new StringBuilder();
        int markerStart = -1;
        var chunk = new char[256];
        while (true)
        {
            int read = await process.StandardOutput.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
                throw new IOException(DescribeExit(process));
            output.Append(chunk, 0, read);
            if (output.Length > 65536)
                throw new IOException("qalc produced more than 64 KB for one expression.");

            if (markerStart < 0)
                markerStart = FindSentinelLineStart(output, sentinel);
            if (markerStart >= 0 && HasPromptAfter(output, markerStart))
                return ExtractResult(output.ToString(), markerStart);
        }
    }

    private static int FindSentinelLineStart(StringBuilder output, string sentinel)
    {
        string text = output.ToString();
        int lineStart = 0;
        while (lineStart < text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) return -1;
            var line = AnsiEscape.Replace(text[lineStart..lineEnd], "").Trim('\r', ' ', '\t');
            if (line.StartsWith('>')) line = line[1..].TrimStart();
            if (line == sentinel) return lineStart;
            lineStart = lineEnd + 1;
        }
        return -1;
    }

    private static bool HasPromptAfter(StringBuilder output, int markerStart)
    {
        for (int i = markerStart; i + 2 < output.Length; i++)
        {
            if (output[i] == '\n' && output[i + 1] == '>' && output[i + 2] == ' ')
                return true;
        }
        return false;
    }

    private static string ExtractResult(string output, int markerStart)
    {
        var beforeMarker = output[..markerStart];
        var lines = beforeMarker.Split('\n').Skip(1)
            .Select(line => AnsiEscape.Replace(line, "").Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .Select(line => line.StartsWith('>') ? line[1..].TrimStart() : line)
            .ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task DrainStandardErrorAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            System.Diagnostics.Debug.WriteLine($"qalc: {line}");
    }

    private static string DescribeExit(Process process)
    {
        try { return $"qalc exited unexpectedly with code {process.ExitCode}."; }
        catch (InvalidOperationException) { return "qalc exited unexpectedly."; }
    }

    private async Task RestartAsync()
    {
        DisposeProcess();
        _assignedVariables.Clear();
        if (!_disposed)
            await EnsureStartedAsync(CancellationToken.None);
    }

    private async Task RestartAfterExitAsync(Process exitedProcess)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (_disposed || !ReferenceEquals(_process, exitedProcess)) return;
                DisposeProcess();
                _assignedVariables.Clear();
                await EnsureStartedAsync(CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not restart qalc: {ex.Message}");
        }
    }

    private void DisposeProcess()
    {
        var process = _process;
        _process = null;
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeProcess();
    }
}
