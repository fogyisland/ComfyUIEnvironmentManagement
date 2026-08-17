using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.17.3: EnvLogRolloverWriter 测试 —— 跨午夜自动切文件,同一天复用 writer。
/// 修"上午开 LogViewer 窗口空"bug:旧实现 AttachStdoutReader 启动期捕获路径,
/// 跨午夜后写仍往昨天文件,LogViewer 今天读到空文件。
///
/// 测试 seam:EnvLogRolloverWriter 接受 path resolver delegate,把 DateTime 注入
/// 固定值,测试可以"时间穿越"到下一天验证 rollover —— 用 mutable clock。
/// </summary>
public class EnvLogRolloverWriterTests : IDisposable
{
    private readonly string _logsDir;
    private const string EnvName = "firstEnv";

    public EnvLogRolloverWriterTests()
    {
        _logsDir = Path.Combine(Path.GetTempPath(), $"envlog-rollover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_logsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logsDir, recursive: true); } catch { }
    }

    private EnvLogRolloverWriter NewWriter(DateTime clock)
    {
        // 闭包捕获 _logsDir + clock,clock 通过字段 mutable 让测试可"穿越时间"
        var resolver = (string env, string _, DateTime? _) =>
            AppLogger.OperationLogPath(env, clock, _logsDir);
        return new EnvLogRolloverWriter(EnvName, resolver, _logsDir);
    }

    [Fact]
    public void WriteLine_CreatesFileInExpectedSubdir()
    {
        var path = AppLogger.OperationLogPath(EnvName, DateTime.Now, _logsDir);
        using (var w = NewWriter(DateTime.Now))
        {
            w.WriteLine("first line");
        }  // dispose before File.ReadAllLines(w holds exclusive lock on FileStream)

        Assert.True(File.Exists(path), $"expected file at {path}");
        Assert.Equal(new[] { "first line" }, File.ReadAllLines(path));
    }

    [Fact]
    public void WriteLine_MultipleLines_AllInSameFile()
    {
        var path = AppLogger.OperationLogPath(EnvName, DateTime.Now, _logsDir);
        using (var w = NewWriter(DateTime.Now))
        {
            w.WriteLine("line 1");
            w.WriteLine("line 2");
            w.WriteLine("line 3");
        }

        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void WriteLine_PathChangesAcrossMidnight_RotatesToNewFile()
    {
        // 用 mutable clock:同一天写 → 复用 writer;改 clock 到第二天 → 自动 rollover。
        var day1 = new DateTime(2026, 8, 12, 23, 50, 0);
        var day2 = new DateTime(2026, 8, 13, 0, 1, 0);
        DateTime clock = day1;
        var resolver = (string env, string _, DateTime? _) =>
            AppLogger.OperationLogPath(env, clock, _logsDir);

        // block dispose 确保两个文件 lock 在 ReadAllLines 之前释放
        using (var w = new EnvLogRolloverWriter(EnvName, resolver, _logsDir))
        {
            // 第一天写
            w.WriteLine("day1 line A");

            // 跨午夜 — mutable clock 切到 day2,下一次写自动滚到新文件
            clock = day2;
            w.WriteLine("day2 line A");
        }

        // 验证两天文件独立
        var day1Path = AppLogger.OperationLogPath(EnvName, day1, _logsDir);
        var day2Path = AppLogger.OperationLogPath(EnvName, day2, _logsDir);
        Assert.NotEqual(day1Path, day2Path);
        Assert.True(File.Exists(day1Path), $"day1 file missing: {day1Path}");
        Assert.True(File.Exists(day2Path), $"day2 file missing: {day2Path}");
        Assert.Equal(new[] { "day1 line A" }, File.ReadAllLines(day1Path));
        Assert.Equal(new[] { "day2 line A" }, File.ReadAllLines(day2Path));
    }

    [Fact]
    public void WriteLine_SameDay_ReusesWriter_NoExtraFiles()
    {
        // 同一天多次写 → 单文件追加模式(不切文件),文件 append 不会出现 partial 行
        var path = AppLogger.OperationLogPath(EnvName, DateTime.Now, _logsDir);
        using (var w = NewWriter(DateTime.Now))
        {
            w.WriteLine("a");
            w.WriteLine("b");
            w.WriteLine("c");
        }

        Assert.Equal(new[] { "a", "b", "c" }, File.ReadAllLines(path));
    }

    [Fact]
    public async Task WriteLineAsync_Awaitable_AppendsToFile()
    {
        // v0.6.17.3:ProcessLauncher AttachStdoutReader 用 WriteLineAsync(async path)
        var path = AppLogger.OperationLogPath(EnvName, DateTime.Now, _logsDir);
        using (var w = NewWriter(DateTime.Now))
        {
            await w.WriteLineAsync("async line 1");
            await w.WriteLineAsync("async line 2");
        }

        Assert.Equal(new[] { "async line 1", "async line 2" }, File.ReadAllLines(path));
    }

    [Fact]
    public void Dispose_ClosesWriter_FurtherWritesAreSilentlyDropped()
    {
        var path = AppLogger.OperationLogPath(EnvName, DateTime.Now, _logsDir);
        var w = NewWriter(DateTime.Now);
        w.WriteLine("before dispose");
        w.Dispose();

        // 设计选择:Dispose 后 WriteLine 静默 no-op(reader 循环继续跑,日志丢 ≤ 1 行可接受)
        w.WriteLine("after dispose");
        var lines = File.ReadAllLines(path);
        Assert.Single(lines);
        Assert.Equal("before dispose", lines[0]);
    }
}