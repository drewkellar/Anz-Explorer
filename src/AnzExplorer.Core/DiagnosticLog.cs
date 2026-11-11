using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AnzExplorer.Core;

/// <summary>
/// Local, crash-resilient diagnostic log. Every entry is flushed to disk as it is written, so
/// when the process is killed by a native fail-fast (which skips all managed crash handlers)
/// the end of the session log still shows what the user was doing.
/// </summary>
/// <remarks>
/// File and folder names are never written to breadcrumbs: paths are reduced to their shape
/// (see <see cref="DescribePath"/>) and path-like text in messages is redacted. Crash reports
/// keep the full exception text because it is needed to diagnose the failure.
/// </remarks>
public sealed partial class DiagnosticLogger : IDisposable
{
    public const string SessionEndMarker = "=== Session ended normally";
    private const int RingCapacity = 300;
    private const long MaximumSessionBytes = 5 * 1024 * 1024;
    private const int KeepSessionLogs = 10;
    private const int KeepCrashReports = 20;
    private const int FirstChanceBurstLimit = 40;
    private static readonly TimeSpan FirstChanceWindow = TimeSpan.FromSeconds(10);

    [ThreadStatic] private static bool _writing;

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private StreamWriter? _writer;
    private long _bytesWritten;
    private bool _truncated;
    private long _firstChanceWindowStart;
    private int _firstChanceInWindow;
    private int _firstChanceSuppressed;

    public DiagnosticLogger(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
        Directory.CreateDirectory(directory);
        PreviousUncleanSessionLog = FindUncleanPreviousSession();
        Prune($"{prefix}-session-*.log", KeepSessionLogs - 1);
        Prune($"{prefix}-crash-*.log", KeepCrashReports);

        SessionLogPath = Path.Combine(
            directory,
            $"{prefix}-session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        var stream = new FileStream(
            SessionLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        Info("Session", $"Started {DescribeEnvironment()}");
        if (PreviousUncleanSessionLog is not null)
        {
            Warn("Session", $"Previous session did not end normally; see {Path.GetFileName(PreviousUncleanSessionLog)}");
        }
    }

    public string SessionLogPath { get; }
    public string? PreviousUncleanSessionLog { get; }
    public string LogDirectory => _directory;

    public IReadOnlyList<string> RecentEntries
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    public void Info(string category, string message) => Write("INFO ", category, message);

    public void Warn(string category, string message, Exception? exception = null) =>
        Write("WARN ", category, exception is null ? message : $"{message} [{DescribeException(exception)}]");

    public void Error(string category, string message, Exception? exception = null) =>
        Write("ERROR", category, exception is null ? message : $"{message} [{DescribeException(exception)}]");

    /// <summary>
    /// Records a first-chance exception (type, HRESULT, redacted message and top frames).
    /// Native XAML fail-fasts often follow a managed exception that was converted to an
    /// HRESULT, so these entries are frequently the only clue left in the log.
    /// </summary>
    public void RecordFirstChance(Exception exception)
    {
        // Anything below may itself throw; the guard stops that from re-entering here.
        if (_writing || exception is OperationCanceledException) return;
        _writing = true;
        try
        {
            lock (_gate)
            {
                var now = _uptime.ElapsedMilliseconds;
                if (now - _firstChanceWindowStart > FirstChanceWindow.TotalMilliseconds)
                {
                    if (_firstChanceSuppressed > 0)
                    {
                        WriteCore("INFO ", "FirstChance", $"{_firstChanceSuppressed} further exception(s) suppressed");
                    }
                    _firstChanceWindowStart = now;
                    _firstChanceInWindow = 0;
                    _firstChanceSuppressed = 0;
                }

                if (++_firstChanceInWindow > FirstChanceBurstLimit)
                {
                    _firstChanceSuppressed++;
                    return;
                }
            }

            string frames;
            try
            {
                frames = string.Join(" <- ", new StackTrace(exception, false).GetFrames()
                    .Take(4)
                    .Select(frame => frame.GetMethod() is { } method
                        ? $"{method.DeclaringType?.Name}.{method.Name}"
                        : "?"));
            }
            catch (Exception)
            {
                frames = "?";
            }

            var message = $"{DescribeException(exception)} at {frames}";
            lock (_gate) WriteCore("DEBUG", "FirstChance", message);
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>Writes a crash report with the full exception and the recent breadcrumbs.</summary>
    public string? WriteCrashReport(string source, Exception? exception)
    {
        try
        {
            Error("Crash", $"{source}: {(exception is null ? "unknown" : DescribeException(exception))}");
            var path = Path.Combine(_directory, $"{_prefix}-crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.log");
            var report = new StringBuilder()
                .AppendLine($"Source: {source}")
                .AppendLine($"Time (UTC): {DateTime.UtcNow:O}")
                .AppendLine($"Uptime: {_uptime.Elapsed}")
                .AppendLine($"Environment: {DescribeEnvironment()}")
                .AppendLine($"Session log: {Path.GetFileName(SessionLogPath)}")
                .AppendLine()
                .AppendLine("Exception:")
                .AppendLine(exception?.ToString() ?? "(none)")
                .AppendLine()
                .AppendLine($"Last {RingCapacity} log entries:");
            foreach (var entry in RecentEntries) report.AppendLine(entry);
            File.WriteAllText(path, report.ToString());
            return path;
        }
        catch (Exception)
        {
            return null; // Crash reporting must never cause a second failure.
        }
    }

    public void EndSession()
    {
        Info("Session", $"Ending after {_uptime.Elapsed:hh\\:mm\\:ss}");
        lock (_gate)
        {
            if (_writer is null) return;
            // Written even past the size limit: its absence is what flags a crash next launch.
            try { _writer.WriteLine(SessionEndMarker); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Describes a path by shape only, e.g. "local(C:) depth=4 len=58".</summary>
    public static string DescribePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "none";
        if (path.StartsWith("anz://", StringComparison.OrdinalIgnoreCase)) return path;
        var kind = path.StartsWith(@"\\", StringComparison.Ordinal) ? "unc"
            : path.Length >= 2 && path[1] == ':' ? $"local({char.ToUpperInvariant(path[0])}:)"
            : "other";
        var depth = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length - (kind == "unc" ? 2 : 1);
        return $"{kind} depth={Math.Max(0, depth)} len={path.Length}";
    }

    /// <summary>Exception type, HRESULT and a path-redacted message.</summary>
    public static string DescribeException(Exception exception)
    {
        var builder = new StringBuilder(exception.GetType().Name)
            .Append(" 0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
            .Append(": ").Append(Redact(exception.Message));
        if (exception.InnerException is { } inner)
        {
            builder.Append(" -> ").Append(inner.GetType().Name).Append(": ").Append(Redact(inner.Message));
        }
        return builder.ToString();
    }

    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : PathPattern().Replace(text, "<path>").ReplaceLineEndings(" ");

    // Drive-letter paths, UNC paths and \\?\ extended paths.
    [GeneratedRegex(@"(?:\\\\\?\\)?(?:[A-Za-z]:\\|\\\\)[^\s'""<>|*?]*")]
    private static partial Regex PathPattern();

    private void Write(string level, string category, string message)
    {
        if (_writing) return;
        _writing = true;
        try
        {
            lock (_gate) WriteCore(level, category, message);
        }
        finally
        {
            _writing = false;
        }
    }

    // Caller holds _gate.
    private void WriteCore(string level, string category, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:HH:mm:ss.fff} +{_uptime.Elapsed.TotalSeconds,8:0.000}s [{level}] T{Environment.CurrentManagedThreadId,-3} {category}: {Redact(message)}");
        _recent.Enqueue(line);
        while (_recent.Count > RingCapacity) _recent.Dequeue();
        WriteRaw(line);
    }

    private void WriteRaw(string line)
    {
        if (_writer is null || _truncated) return;
        try
        {
            if (_bytesWritten > MaximumSessionBytes)
            {
                _truncated = true;
                _writer.WriteLine("=== Log size limit reached; further entries are kept in memory for crash reports only.");
                return;
            }
            _writer.WriteLine(line);
            _bytesWritten += line.Length + 2;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            _truncated = true; // Disk full or file removed: keep running without file logging.
        }
    }

    private string? FindUncleanPreviousSession()
    {
        try
        {
            var previous = new DirectoryInfo(_directory)
                .EnumerateFiles($"{_prefix}-session-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (previous is null || IsOwnedByRunningProcess(previous.Name)) return null;

            using var stream = new FileStream(
                previous.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 512), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(SessionEndMarker, StringComparison.Ordinal)
                ? null
                : previous.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Another instance still running has an open session without an end marker; that is not a crash.
    private static bool IsOwnedByRunningProcess(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!int.TryParse(stem[(stem.LastIndexOf('-') + 1)..], out var processId)) return false;
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Prune(string pattern, int keep)
    {
        try
        {
            foreach (var file in new DirectoryInfo(_directory).EnumerateFiles(pattern)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(Math.Max(0, keep)))
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention is best-effort.
        }
    }

    private static string DescribeEnvironment()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        using var process = Process.GetCurrentProcess();
        return string.Create(CultureInfo.InvariantCulture,
            $"app={version} os={Environment.OSVersion.Version} arch={RuntimeInformation.ProcessArchitecture} " +
            $".net={Environment.Version} pid={Environment.ProcessId} workingSetMB={process.WorkingSet64 / (1024 * 1024)}");
    }
}

/// <summary>Process-wide access point. Calls are no-ops until <see cref="Initialize"/> runs.</summary>
public static class DiagnosticLog
{
    public static DiagnosticLogger? Current { get; private set; }

    public static DiagnosticLogger Initialize(string directory, string prefix)
    {
        Current ??= new DiagnosticLogger(directory, prefix);
        return Current;
    }

    /// <summary>Hooks process-wide exception sources: first-chance, unhandled and unobserved task exceptions.</summary>
    public static void HookProcessExceptions()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Current?.WriteCrashReport("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Current?.Error("Task", "Unobserved task exception", args.Exception);
    }

    public static void Info(string category, string message) => Current?.Info(category, message);

    public static void Warn(string category, string message, Exception? exception = null) =>
        Current?.Warn(category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Current?.Error(category, message, exception);

    private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
    {
        try
        {
            Current?.RecordFirstChance(args.Exception);
        }
        catch (Exception)
        {
            // Never let diagnostics change program behavior.
        }
    }
}
