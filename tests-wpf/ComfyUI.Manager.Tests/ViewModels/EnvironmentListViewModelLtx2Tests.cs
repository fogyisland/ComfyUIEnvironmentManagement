using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-08-30) Task 8:EnvironmentListViewModel 接 ModelsMissingException
/// → 弹 MessageBox(标题 "LTX-2 模型缺失",内容含 HF URL + hf download 命令)。
///
/// 复用现有 StartEnvForTest seam(已有,ProcessLauncher sealed)代替 IProcessLauncher
/// interface 抽取 — 跟 EnvironmentListViewModelReopenStartStatusTests 同样 pattern。
///
/// MessageBox 注入通过新增 ctor 参数 <c>messageBoxAsync: Func&lt;string,string,Task&gt;?</c>,
/// null fallback 走 System.Windows.MessageBox.Show(生产)。
/// </summary>
public sealed class EnvironmentListViewModelLtx2Tests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelLtx2Tests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistvm-ltx2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// 简化版 messageBox 录制:title/message 记录 + call 计数。Func 签名跟
    /// EnvironmentListViewModel 新 ctor 参数一致(title, msg → Task)。
    /// </summary>
    private sealed class RecordingMessageBox
    {
        public int ShowCalls { get; private set; }
        public string? LastTitle { get; private set; }
        public string? LastMessage { get; private set; }
        public Task InvokeAsync(string title, string message)
        {
            ShowCalls++;
            LastTitle = title;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }

    private Environment SeedEnv(string id, string status)
    {
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = Path.Combine(_tempRoot, id),
            ComfyuiLayout = "isolated",
            TemplateKind = "LTXVideo",
            ModelsDirectory = Path.Combine(_tempRoot, "models"),
            TemplateConfigSnapshot = new TemplateConfig { Kind = "LTXVideo", Name = "LTXVideo" },
            Status = status,
        };
        Directory.CreateDirectory(env.RootPath);
        _repo.Upsert(env);
        return env;
    }

    /// <summary>
    /// NewVm + StartEnvForTest seam 设上 + messageBoxAsync 注入。
    /// onStart 接收 (env, stageProgress, logProgress, ct) — 跟现有 seam 一致。
    /// </summary>
    private EnvironmentListViewModel NewVm(
        RecordingMessageBox msgbox,
        Func<Environment, IProgress<string>?, IProgress<string>?, CancellationToken, Task> onStart)
    {
        var vm = new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!, null!, null!, null!,
            _tempRoot, null!,
            null!, null!, null!, null!, null!, null!,
            new CatalogRepository(new CatalogCacheStore(_db.Path)),
            new NodeRepository(_db.Factory),
            new NodeVersionRepository(new CatalogCacheStore(_db.Path)),
            null!, null!, null!, null!,
            messageBoxAsync: msgbox.InvokeAsync);
        vm.StartEnvForTest = onStart;
        return vm;
    }

    /// <summary>
    /// 调 StartCommand → 内部 async StartEnvAsync,等任务跑完再 assert。
    /// RelayCommand 异步触发(Execute handler 是 async lambda),Task 不直接返回,
    /// 所以轮询 StartStatus.IsVisible(begin 后 true) + 等 50ms 让 finally(Load +
    /// RaiseCommandsChanged)跑完。镜像 EnvironmentListViewModelReopenStartStatusTests
    /// .InvokeStartAsync 同样的 pattern。
    /// </summary>
    private static async Task InvokeStartAsync(EnvironmentListViewModel vm, Environment env)
    {
        vm.StartCommand.Execute(env);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (vm.StartStatus is null)
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException("StartStatus never set");
            await Task.Delay(20);
        }
        await Task.Delay(50);  // 等 finally 块(Load + RaiseCommandsChanged)跑完
    }

    [Fact]
    public async Task StartEnv_ModelsMissing_ShowsMessageBox_DoesNotRethrow()
    {
        var env = SeedEnv("env-ltx-missing", "starting");
        env.Pid = 4242;  // 模拟正在启动被异常打断 — 让 stopped/Pid=null 断言有意义
        var msgbox = new RecordingMessageBox();
        var vm = NewVm(msgbox, (_, _, _, _) =>
        {
            throw new ModelsMissingException(
                "缺少 LTX-2 模型文件(2 个)",
                new List<string> { "/a/transformer.safetensors", "/a/vae.safetensors" },
                "https://huggingface.co/Lightricks/LTX-2.5",
                "hf download Lightricks/LTX-2.5 --local-dir models/ltx-2.5");
        });

        await InvokeStartAsync(vm, env);

        // ModelsMissingException catch 命中:_messageBoxAsync 弹了 1 次,内容含 HF info。
        Assert.Equal(1, msgbox.ShowCalls);
        Assert.Equal("LTX-2 模型缺失", msgbox.LastTitle);
        Assert.Contains("huggingface.co/Lightricks/LTX-2.5", msgbox.LastMessage);
        Assert.Contains("hf download", msgbox.LastMessage);
        // env 回到 stopped(Pid 清空后下一次 Load 不复活) — 播种 starting + Pid=4242
        // 才能真正断 catch 后清理生效,否则删生产两行也绿。
        Assert.Equal("stopped", env.Status);
        Assert.Null(env.Pid);
    }

    [Fact]
    public async Task StartEnv_NonModelsMissingException_StillShowsGenericError()
    {
        var env = SeedEnv("env-ltx-generic", "stopped");
        var msgbox = new RecordingMessageBox();
        var vm = NewVm(msgbox, (_, _, _, _) =>
        {
            throw new InvalidOperationException("generic error");
        });

        await InvokeStartAsync(vm, env);

        // 非 ModelsMissingException:走 generic catch + status.Fail(状态面板显示错误),
        // 不弹 MessageBox 也不设置 env.Status(原行为不变 — brief 注释「现有通用 catch」)。
        // 关键 invariant:MessageBox 内容不会污染成 ModelsMissingException 的 HF 信息。
        Assert.Equal(0, msgbox.ShowCalls);
        Assert.Null(msgbox.LastMessage);
        // 镜像 EnvironmentListViewModelReopenStartStatusTests:110-111 既有写法,
        // StartStatus 面板上能看到错误文案。
        Assert.NotNull(vm.StartStatus!.Error);
        Assert.Contains("generic error", vm.StartStatus.Error);
    }
}
