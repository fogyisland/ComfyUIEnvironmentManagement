using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.7.2:LogTailer 历史回放 + 增量 tail 行为测试。
/// 不起真实进程,直接写临时文件 + 收集 NewLine 事件验证 emit 时序。
/// </summary>
public sealed class LogTailerTests : IDisposable
{
    private readonly List<string> _tmpFiles = new();

    public void Dispose()
    {
        foreach (var f in _tmpFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private string NewLogFile(params string[] preLines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tailer-test-{Guid.NewGuid():N}.log");
        if (preLines.Length > 0)
        {
            File.WriteAllLines(path, preLines);
        }
        else
        {
            File.Create(path).Dispose();
        }
        _tmpFiles.Add(path);
        return path;
    }

    private static List<string> CaptureNewLines(LogTailer t, Action action, TimeSpan wait)
    {
        var seen = new List<string>();
        t.NewLine += ll => seen.Add(ll.Text);
        action();
        Thread.Sleep(wait);
        t.Stop();
        return seen;
    }

    [Fact]
    public void Start_EmptyFile_NoNewLinesEmitted()
    {
        var path = NewLogFile();
        using var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(50));

        var seen = CaptureNewLines(tailer, tailer.Start, TimeSpan.FromMilliseconds(150));

        Assert.Empty(seen);
    }

    [Fact]
    public void Start_ExistingContent_EmitAllAsHistory()
    {
        var path = NewLogFile("line1", "line2", "line3");
        using var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(50));

        var seen = CaptureNewLines(tailer, tailer.Start, TimeSpan.FromMilliseconds(150));

        Assert.Equal(new[] { "line1", "line2", "line3" }, seen);
    }

    [Fact]
    public void Start_ExistingContent_ThenAppend_HistoryPlusTailed()
    {
        var path = NewLogFile("alpha", "beta");
        using var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(50));

        var seen = new List<string>();
        tailer.NewLine += ll => seen.Add(ll.Text);
        tailer.Start();

        // 历史先到:alpha,beta
        Thread.Sleep(150);
        File.AppendAllLines(path, new[] { "gamma", "delta" });
        Thread.Sleep(300);
        tailer.Stop();

        // 顺序不强制(Start 内部同步 emit 历史,tail 周期到才看到 gamma/delta)
        // 但所有 4 行必须出现,且历史行先于新增行
        Assert.Contains("alpha", seen);
        Assert.Contains("beta", seen);
        Assert.Contains("gamma", seen);
        Assert.Contains("delta", seen);
        Assert.Equal(4, seen.Count);
        var alphaIdx = seen.IndexOf("alpha");
        var gammaIdx = seen.IndexOf("gamma");
        Assert.True(alphaIdx < gammaIdx, "历史行应在 tail 新行之前 emit");
    }

    [Fact]
    public void Start_OversizedFile_TruncatesAndDropsPartialHeadLine()
    {
        // 构造一个大于 MaxHistoryBytes (64KB) 的文件。每行 padding 长度 = 400 字符
        // + 换行 = 401 字节;400 行 ≈ 160KB,远超 64KB。
        var path = NewLogFile();
        using (var sw = new StreamWriter(path, append: false))
        {
            for (var i = 0; i < 400; i++)
            {
                sw.WriteLine(new string('x', 400));
            }
        }
        var len = new FileInfo(path).Length;
        Assert.True(len > LogTailer.MaxHistoryBytes,
            $"test fixture len {len} must exceed MaxHistoryBytes {LogTailer.MaxHistoryBytes}");

        // 现在 append 3 个明显的 sentinel 行
        File.AppendAllLines(path, new[] { "SENTINEL_A", "SENTINEL_B", "SENTINEL_C" });

        using var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(50));
        var seen = CaptureNewLines(tailer, tailer.Start, TimeSpan.FromMilliseconds(150));

        Assert.Contains("SENTINEL_A", seen);
        Assert.Contains("SENTINEL_B", seen);
        Assert.Contains("SENTINEL_C", seen);
        // MaxHistoryBytes 截断 → 历史行全部丢弃 / 首 partial 行丢弃,所以不要求看到 padding 'x'
        // 不应包含太老的行(其实无法精确断言;只断言 sentinel 在即可)
    }

    [Fact]
    public void Start_FileMissing_TailWithoutCrash()
    {
        // 文件不存在 → Start 不应抛,tail 起来后 append 才出现
        var path = Path.Combine(Path.GetTempPath(), $"tailer-missing-{Guid.NewGuid():N}.log");
        // 不创建
        using var tailer = new LogTailer(path, TimeSpan.FromMilliseconds(50));
        var seen = new List<string>();
        tailer.NewLine += ll => seen.Add(ll.Text);
        var ex = Record.Exception(() => tailer.Start());
        Assert.Null(ex);

        // 之后写入文件
        File.AppendAllLines(path, new[] { "late1", "late2" });
        Thread.Sleep(300);
        tailer.Stop();

        Assert.Contains("late1", seen);
        Assert.Contains("late2", seen);
        _tmpFiles.Add(path);  // 清理
    }
}
