using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0 sidebar.inf 控制侧栏启用。ManagerSidebarConfig 负责:
/// - 文件不存在 → 写默认模板(所有启用,但留好 hooks)
/// - 文件存在 → 解析并缓存
/// - IsEnabled(MainSection) → missing 默认 true
/// 测试用临时目录,完全隔离,不碰全局静态。
/// </summary>
public class ManagerSidebarConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ManagerSidebarConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sidebar_cfg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "sidebar.inf");
        // ManagerSidebarConfig 是 static,必须每个测试前 Reset 避免测试间污染
        ManagerSidebarConfig.Reset();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Initialize_FileMissing_DoesNotCreate_AllEnabledByDefault()
    {
        // v1.0.0:sidebar.inf 是 release seed,App 不自动生成。
        // 缺失 → 静默,所有按钮默认启用(缺文件 = 全 True)。
        Assert.False(File.Exists(_file));
        var result = ManagerSidebarConfig.Initialize(_file);
        Assert.False(result.FileExists);
        foreach (MainSection s in Enum.GetValues<MainSection>())
        {
            Assert.True(ManagerSidebarConfig.IsEnabled(s), $"{s} should be enabled when file missing");
        }
    }

    [Fact]
    public void Initialize_FileExists_AppliesValues()
    {
        File.WriteAllText(_file, "Dashboard=0\nEnvironments=1\n");
        var result = ManagerSidebarConfig.Initialize(_file);
        Assert.True(result.FileExists);
        Assert.False(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Environments));
    }

    [Fact]
    public void IsEnabled_MissingKey_DefaultsToTrue()
    {
        File.WriteAllText(_file, "Dashboard=0\n");
        ManagerSidebarConfig.Initialize(_file);
        // 没写的 key → 全启用
        Assert.False(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Environments));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Catalog));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Workflows));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Models));
    }

    [Fact]
    public void IsEnabled_AllSectionsCanBeDisabled()
    {
        var text = "Dashboard=0\nEnvironments=0\nCatalog=0\nLocalNodes=0\nWorkflows=0\nTemplates=0\nModels=0\nSettings=0\nBulkUpdate=0\nSystemStatus=0\n";
        File.WriteAllText(_file, text);
        ManagerSidebarConfig.Initialize(_file);
        foreach (MainSection s in Enum.GetValues<MainSection>())
        {
            Assert.False(ManagerSidebarConfig.IsEnabled(s), $"{s} should be disabled");
        }
    }

    [Fact]
    public void Initialize_CalledTwice_SecondCallIsNoop()
    {
        File.WriteAllText(_file, "Dashboard=0\n");
        ManagerSidebarConfig.Initialize(_file); // 第一次读 → Dashboard=0
        File.WriteAllText(_file, "Dashboard=1\n"); // 立刻改
        ManagerSidebarConfig.Initialize(_file); // 第二次不重读
        // 缓存还是第一次的 Dashboard=0
        Assert.False(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
    }

    [Fact]
    public void Initialize_AfterReset_CanReload()
    {
        File.WriteAllText(_file, "Dashboard=0\n");
        ManagerSidebarConfig.Initialize(_file);
        Assert.False(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));

        File.WriteAllText(_file, "Dashboard=1\n");
        ManagerSidebarConfig.Reset();
        ManagerSidebarConfig.Initialize(_file);
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
    }

    [Fact]
    public void Initialize_FileMalformed_DoesNotThrow()
    {
        File.WriteAllText(_file, "garbage line\n=garbage\nWorkflows=UNDEFINED\n");
        // 解析容错:所有坏行都跳过 → 不抛。
        // Workflows=UNDEFINED 跳过 → 不在 dict → missing key 默认 true。
        var ex = Record.Exception(() => ManagerSidebarConfig.Initialize(_file));
        Assert.Null(ex);
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Workflows));
    }

    [Fact]
    public void IsEnabled_BeforeInitialize_DefaultsToTrue()
    {
        ManagerSidebarConfig.Reset();
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Dashboard));
        Assert.True(ManagerSidebarConfig.IsEnabled(MainSection.Catalog));
    }
}
