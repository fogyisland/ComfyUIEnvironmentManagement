using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests;

/// <summary>
/// v0.6.15 T6:验证 <c>App.xaml.cs</c> 传给 MainViewModel 的依赖足够构造
/// <see cref="LocalNodeService"/> + <see cref="LocalNodeCopyInstaller"/>。
/// <para>
/// 不跑 App.xaml.cs 整段(那是 App-level,启动 WPF + splash + MainWindow),
/// 只验 ctor 依赖可注入 + 不抛 — 这是 DI 正确性的最低保障。
/// </para>
/// </summary>
public class AppStartupWiringTests
{
    [Fact]
    public void LocalNodeService_And_LocalNodeCopyInstaller_DependenciesCanBeConstructed()
    {
        using var db = new TestDb();
        var dbFactory = new SqliteConnectionFactory(db.Path);
        var envRepo = new EnvironmentRepository(dbFactory);
        var nodeRepo = new NodeRepository(dbFactory);
        var settings = new Settings { LocalNodeDirectory = Path.Combine(Path.GetTempPath(), "x") };
        var diffService = new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", "")));
        var nodeOps = new NodeOperations(new GitRunner("git"), envRepo, nodeRepo, settings, diffService);

        // 不抛 = DI 依赖足够
        var svc = new LocalNodeService(settings, nodeRepo, envRepo, nodeOps);
        var installer = new LocalNodeCopyInstaller(envRepo, nodeRepo, nodeOps);

        Assert.NotNull(svc);
        Assert.NotNull(installer);
    }
}
