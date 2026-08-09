using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class AppLoggerReadRecentLinesTests : IDisposable
{
    private readonly string _tempRoot;

    public AppLoggerReadRecentLinesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"applogger-recent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void ReadRecentLines_OneDay_ReturnsTodayOnly()
    {
        using var logger = new AppLogger(_tempRoot);
        WriteLog(DateTime.Now, "one\ntwo\nthree\n");

        var lines = logger.ReadRecentLines(daysBack: 1, maxLines: 5).ToArray();

        Assert.Equal(new[] { "three", "two", "one" }, lines);
    }

    [Fact]
    public void ReadRecentLines_TwoDays_MergesTodayAndYesterday()
    {
        using var logger = new AppLogger(_tempRoot);
        WriteLog(DateTime.Now, "today-old\ntoday-new\n");
        WriteLog(DateTime.Now.AddDays(-1), "yesterday-old\nyesterday-new\n");

        var lines = logger.ReadRecentLines(daysBack: 2, maxLines: 5).ToArray();

        Assert.Equal(new[] { "today-new", "today-old", "yesterday-new", "yesterday-old" }, lines);
    }

    [Fact]
    public void ReadRecentLines_MissingFile_SkipsAndReturnsRest()
    {
        using var logger = new AppLogger(_tempRoot);
        WriteLog(DateTime.Now, "available\n");

        var lines = logger.ReadRecentLines(daysBack: 7, maxLines: 5).ToArray();

        Assert.Equal(new[] { "available" }, lines);
    }

    private void WriteLog(DateTime date, string content)
    {
        var logDir = Path.Combine(_tempRoot, "Logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, $"{date:yyyy-MM-dd}.log"), content);
    }
}
