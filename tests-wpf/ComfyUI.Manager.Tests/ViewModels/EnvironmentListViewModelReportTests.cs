using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.7 T2:env-list 行内 "组件报告" 按钮的 UI 集成测试。
/// 覆盖 6 个场景:null-env / 写文件 + 打开 / 文件名 sanitization / builder 抛错 / busy-can-execute / idle-can-execute。
/// </summary>
public class EnvironmentListViewModelReportTests
{
    private static EnvironmentListViewModel MakeVm(TestDb db, string projectRoot)
    {
        var profileLoader = new BaseEnvProfileLoader(
            Path.Combine(Path.GetTempPath(), "report-test-loader-" + Guid.NewGuid()));
        var vm = new EnvironmentListViewModel(
            new EnvironmentRepository(db.Factory),
            null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            projectRoot,
            null!);
        return vm;
    }

    private static Environment MakeEnv(string id, string? name = null) =>
        new()
        {
            Id = id,
            Name = name ?? id,
            RootPath = $"C:\\envs\\{id}",
            Status = "stopped",
        };

    private static EnvComponentReport MakeMinimalReport(string envName) =>
        new()
        {
            EnvName = envName,
            GeneratedAtUtc = DateTime.UtcNow,
            AppVersion = "test",
            Required = null,
            KeyPackages = new List<ActualKeyPackage>(),
            FullPipList = new List<PipPackage>(),
            ComfyuiStatus = null,
            CustomNodes = new List<GitTargetStatus>(),
            Metadata = new EnvMetadata { RootPath = "C:\\envs\\" + envName },
            SectionWarnings = new List<string>(),
        };

    /// <summary>
    /// 伪 builder:固定返回 factory 决定的 report,或抛错。subclass EnvComponentReportBuilder
    /// 来短路所有 subprocess。
    /// </summary>
    private sealed class FakeReportBuilder : EnvComponentReportBuilder
    {
        private readonly Func<Environment, EnvComponentReport> _factory;
        public FakeReportBuilder(
            Func<Environment, EnvComponentReport> factory)
            : base(new BaseEnvProfileLoader(Path.Combine(Path.GetTempPath(), "fake-loader-" + Guid.NewGuid())),
                  new FakeRepo(),
                  "git",
                  "test")
        {
            _factory = factory;
        }

        public override Task<EnvComponentReport> BuildAsync(Environment env, CancellationToken ct = default)
            => Task.FromResult(_factory(env));
    }

    private sealed class FakeRepo : IEnvironmentRepository
    {
        public Environment? Get(string id) => null;
        public List<Environment> ListAll() => new();
        public void Upsert(Environment env) { }
        public int? GetMaxPort() => null;
    }

    [Fact]
    public void ReportComponents_NullEnv_DoesNothing()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        vm.Selected = null;

        var builder = new FakeReportBuilder(_ => MakeMinimalReport("noop"));
        vm.ComponentReportBuilderOverride = builder;

        var openedPath = (string?)null;
        vm.OpenReportFileOverride = p => openedPath = p;

        // 不传 parameter,CommandParameter null → 取 Selected (null) → 直接 return。
        vm.ReportComponentsCommand.Execute(null);

        Assert.Null(openedPath);
    }

    [Fact]
    public async Task ReportComponents_WritesHtmlFile_AndOpensIt()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        var env = MakeEnv("e1", name: "e1");
        new EnvironmentRepository(db.Factory).Upsert(env);

        var builder = new FakeReportBuilder(e => MakeMinimalReport(e.Name));
        vm.ComponentReportBuilderOverride = builder;

        var openedPath = (string?)null;
        vm.OpenReportFileOverride = p => openedPath = p;

        vm.ReportComponentsCommand.Execute(env);
        // RelayCommand 是 async void,等 LastReportTask 跑完再断言。
        if (vm.LastReportTask is not null) await vm.LastReportTask;

        Assert.NotNull(openedPath);
        Assert.True(File.Exists(openedPath!), $"expected file to exist at {openedPath}");
        var content = File.ReadAllText(openedPath!);
        Assert.Contains("<html", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportComponents_FileNameSanitized_ForEnvNameWithInvalidChars()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        var env = MakeEnv("e1", name: "my/env:name*?");
        new EnvironmentRepository(db.Factory).Upsert(env);

        var builder = new FakeReportBuilder(e => MakeMinimalReport(e.Name));
        vm.ComponentReportBuilderOverride = builder;

        var openedPath = (string?)null;
        vm.OpenReportFileOverride = p => openedPath = p;

        vm.ReportComponentsCommand.Execute(env);
        if (vm.LastReportTask is not null) await vm.LastReportTask;

        Assert.NotNull(openedPath);
        var fileName = Path.GetFileName(openedPath!);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in invalid)
        {
            Assert.False(fileName.Contains(ch), $"file name '{fileName}' must not contain invalid char '{ch}'");
        }
    }

    [Fact]
    public async Task ReportComponents_BuilderThrows_ShowsMessageBox_DoesNotOpen()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        var env = MakeEnv("e1", name: "e1");
        new EnvironmentRepository(db.Factory).Upsert(env);

        var builder = new FakeReportBuilder(_ => throw new InvalidOperationException("boom"));
        vm.ComponentReportBuilderOverride = builder;

        var lastMessage = (string?)null;
        var lastTitle = (string?)null;
        vm.ShowMessageBoxOverride = (msg, title) =>
        {
            lastMessage = msg;
            lastTitle = title;
        };

        var openedPath = (string?)null;
        vm.OpenReportFileOverride = p => openedPath = p;

        vm.ReportComponentsCommand.Execute(env);
        if (vm.LastReportTask is not null) await vm.LastReportTask;

        Assert.NotNull(lastMessage);
        Assert.NotNull(lastTitle);
        Assert.Contains("失败", lastMessage!);
        Assert.Contains("boom", lastMessage!);
        Assert.Null(openedPath);
    }

    [Fact]
    public void ReportComponents_CanExecute_FalseWhenEnvBusy()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        var env = MakeEnv("e1", name: "e1");
        new EnvironmentRepository(db.Factory).Upsert(env);

        // 模拟 env busy:走反射访问 private _envBusy 字典。BusyKind 索引 5 = Start
        // (跟 EnvironmentListViewModel.cs:39 声明顺序对应)。
        var busyField = typeof(EnvironmentListViewModel).GetField(
            "_envBusy",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var dict = busyField!.GetValue(vm) as System.Collections.IDictionary;
        dict!.Add(env.RootPath, 5);

        Assert.False(vm.ReportComponentsCommand.CanExecute(env));
    }

    [Fact]
    public void ReportComponents_CanExecute_TrueWhenEnvIdle()
    {
        using var db = new TestDb();
        var projectRoot = Path.Combine(Path.GetTempPath(), "report-test-" + Guid.NewGuid());
        var vm = MakeVm(db, projectRoot);
        var env = MakeEnv("e1", name: "e1");
        new EnvironmentRepository(db.Factory).Upsert(env);

        Assert.True(vm.ReportComponentsCommand.CanExecute(env));
    }
}
