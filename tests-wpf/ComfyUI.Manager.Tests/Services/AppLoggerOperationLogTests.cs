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
        // v0.6.17.3: 子目录布局 logs/env/firstEnv/2026-08-12.log(老格式是平面 Logs/operation-firstEnv-...)
        Assert.Equal(Path.Combine(_tmpDir, "logs", "env", "firstEnv", "2026-08-12.log"), path);
    }

    [Fact]
    public void OperationLogPath_SpecialChars_ReplacedWithUnderscore()
    {
        var path = AppLogger.OperationLogPath("foo/bar:baz", new DateTime(2026, 8, 12), _tmpDir);
        // v0.6.17.3: envName 净化结果决定子目录名;filename 现在只有日期
        Assert.Equal(Path.Combine("foo_bar_baz", "2026-08-12.log"), Path.GetRelativePath(Path.Combine(_tmpDir, "logs", "env"), path));
    }

    [Fact]
    public void OperationLogPath_EmptyEnvName_FallsBackToUnknown()
    {
        var path = AppLogger.OperationLogPath("", new DateTime(2026, 8, 12), _tmpDir);
        // 空 envName fallback "unknown" 子目录
        Assert.Equal(Path.Combine(_tmpDir, "logs", "env", "unknown", "2026-08-12.log"), path);
    }

    [Fact]
    public void OperationLogPath_LongEnvName_TruncatedTo100Chars()
    {
        var longName = new string('a', 200);
        var path = AppLogger.OperationLogPath(longName, new DateTime(2026, 8, 12), _tmpDir);
        // 200 a's 截断到 100 → env 子目录名是 100 个 a
        var envDir = Path.GetFileName(Path.GetDirectoryName(path)!);
        Assert.Equal(100, envDir.Length);
        Assert.Equal(new string('a', 100), envDir);
        // 文件名 = yyyy-MM-dd.log(14 字符)
        Assert.Equal("2026-08-12.log", Path.GetFileName(path));
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