using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AnzExplorerTests", "logs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Entries_are_on_disk_immediately_without_dispose()
    {
        // A native fail-fast never runs Dispose; the line must already be in the file.
        var logger = new DiagnosticLogger(_root, "test");
        logger.Info("UI", "Navigate local(C:) depth=2");

        var text = ReadShared(logger.SessionLogPath);
        Assert.Contains("Navigate local(C:) depth=2", text);
        logger.Dispose();
    }

    [Fact]
    public void Missing_end_marker_flags_the_previous_session_as_unclean()
    {
        var crashed = new DiagnosticLogger(_root, "test");
        crashed.Info("UI", "last action before crash");
        crashed.Dispose(); // Simulates a process kill: no EndSession.
        RenameAsDeadProcess(crashed.SessionLogPath);

        using var next = new DiagnosticLogger(_root, "test");

        Assert.NotNull(next.PreviousUncleanSessionLog);
        Assert.Contains(next.RecentEntries, entry => entry.Contains("did not end normally"));
    }

    [Fact]
    public void Clean_end_is_not_reported_as_a_crash()
    {
        var clean = new DiagnosticLogger(_root, "test");
        clean.EndSession();
        RenameAsDeadProcess(clean.SessionLogPath);

        using var next = new DiagnosticLogger(_root, "test");

        Assert.Null(next.PreviousUncleanSessionLog);
    }

    [Fact]
    public void Paths_in_messages_are_redacted()
    {
        using var logger = new DiagnosticLogger(_root, "test");
        logger.Warn("UI", "Failed", new IOException(@"Access to C:\Users\Someone\Secret Plans.txt denied, also \\server\share\x"));

        var entry = logger.RecentEntries[^1];
        Assert.DoesNotContain("Someone", entry);
        Assert.DoesNotContain("server", entry);
        Assert.Contains("<path>", entry);
        Assert.Contains("IOException", entry);
    }

    [Theory]
    [InlineData(@"C:\Users\Someone\Documents", "local(C:) depth=3 len=26")]
    [InlineData(@"\\server\share\folder", "unc depth=1 len=21")]
    [InlineData("anz://this-pc", "anz://this-pc")]
    [InlineData(null, "none")]
    public void DescribePath_reports_shape_only(string? path, string expected) =>
        Assert.Equal(expected, DiagnosticLogger.DescribePath(path));

    [Fact]
    public void Crash_report_contains_exception_and_recent_breadcrumbs()
    {
        using var logger = new DiagnosticLogger(_root, "test");
        logger.Info("UI", "W1 Delete: items=3");

        var path = logger.WriteCrashReport("Test", new InvalidOperationException("boom"));

        Assert.NotNull(path);
        var report = File.ReadAllText(path!);
        Assert.Contains("InvalidOperationException: boom", report);
        Assert.Contains("W1 Delete: items=3", report);
    }

    [Fact]
    public void First_chance_bursts_are_rate_limited()
    {
        using var logger = new DiagnosticLogger(_root, "test");
        for (var index = 0; index < 500; index++) logger.RecordFirstChance(new IOException("noise"));

        var firstChance = logger.RecentEntries.Count(entry => entry.Contains("FirstChance"));
        Assert.InRange(firstChance, 1, 50);
    }

    [Fact]
    public void Old_session_logs_are_pruned()
    {
        for (var index = 0; index < 15; index++)
        {
            var logger = new DiagnosticLogger(_root, "test");
            logger.EndSession();
            RenameAsDeadProcess(logger.SessionLogPath, index);
        }

        Assert.InRange(Directory.GetFiles(_root, "test-session-*.log").Length, 1, 10);
    }

    // Session files embed the PID; rename to an unused PID so the current test process
    // is not mistaken for the still-running owner of the "previous" session.
    private static void RenameAsDeadProcess(string path, int salt = 0)
    {
        var dead = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"test-session-19990101-0000{salt:00}-{int.MaxValue - salt}.log");
        File.Move(path, dead);
        File.SetLastWriteTimeUtc(dead, DateTime.UtcNow.AddMinutes(-60 + salt));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
