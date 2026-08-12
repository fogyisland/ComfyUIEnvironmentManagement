using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.12: AppLogger per-env 日志路径 + 写入。
/// 文件名净化:不允许 \ / : * ? " < > | 以及控制字符,替换为 _;截断 100 字符;空 fallback "unknown"。
/// </summary>
public class AppLoggerOperationLogTests : IDisposable
{
    private readonly string _tmpDir;

    public AppLoggerOperationLogTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"applogger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public void OperationLogPath_RegularEnvName_ReturnsExpectedFormat()
    {
        var path = AppLogger.OperationLogPath("firstEnv", new DateTime(2026, 8, 12), _tmpDir);
        Assert.Equal(Path.Combine(_tmpDir, "Logs", "operation-firstEnv-2026-08-12.log"), path);
    }

    [Fact]
    public void OperationLogPath_SpecialChars_ReplacedWithUnderscore()
    {
        var path = AppLogger.OperationLogPath("foo/bar:baz", new DateTime(2026, 8, 12), _tmpDir);
        var fileName = Path.GetFileName(path);
        Assert.Equal("operation-foo_bar_baz-2026-08-12.log", fileName);
    }

    [Fact]
    public void OperationLogPath_EmptyEnvName_FallsBackToUnknown()
    {
        var path = AppLogger.OperationLogPath("", new DateTime(2026, 8, 12), _tmpDir);
        var fileName = Path.GetFileName(path);
        Assert.Equal("operation-unknown-2026-08-12.log", fileName);
    }

    [Fact]
    public void OperationLogPath_LongEnvName_TruncatedTo100Chars()
    {
        var longName = new string('a', 200);
        var path = AppLogger.OperationLogPath(longName, new DateTime(2026, 8, 12), _tmpDir);
        var fileName = Path.GetFileName(path);
        // G7 spec: sanitized envName ≤ 100 字符。格式固定前缀后缀:
        //   operation-(10) + {sanitized}(≤100) + -(1) + yyyy-MM-dd.log(14) = 125
        Assert.True(fileName.Length <= 125, $"fileName too long: {fileName.Length}");
        Assert.StartsWith("operation-", fileName);
        // 200 a's 应被截断到 100
        Assert.DoesNotContain(new string('a', 101), fileName);
    }

    [Fact]
    public void WriteOperation_CreatesFile_AndAppendsLine()
    {
        var logger = new AppLogger(_tmpDir);
        logger.WriteOperation("firstEnv", "test message 1");
        logger.WriteOperation("firstEnv", "test message 2");

        var path = AppLogger.OperationLogPath("firstEnv", DateTime.Now, _tmpDir);
        Assert.True(File.Exists(path), $"Expected file at {path}");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("test message 1", lines[0]);
        Assert.Contains("test message 2", lines[1]);
    }

    [Fact]
    public void WriteOperation_DifferentEnvNames_WritesToDifferentFiles()
    {
        var logger = new AppLogger(_tmpDir);
        logger.WriteOperation("envA", "msg A");
        logger.WriteOperation("envB", "msg B");

        var pathA = AppLogger.OperationLogPath("envA", DateTime.Now, _tmpDir);
        var pathB = AppLogger.OperationLogPath("envB", DateTime.Now, _tmpDir);
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.Single(File.ReadAllLines(pathA));
        Assert.Single(File.ReadAllLines(pathB));
    }
}