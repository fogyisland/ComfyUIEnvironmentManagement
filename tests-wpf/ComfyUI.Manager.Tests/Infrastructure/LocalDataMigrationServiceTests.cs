using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.16: 验证一次性数据迁移 service —— 把旧目录(模拟 %APPDATA%/ComfyUI-Manager/)
/// 复制到 &lt;projectRoot&gt;/.manager/。
///
/// 每个 test 用唯一 temp dir 模拟旧 + 新目录,避免污染真实 APPDATA。
/// 通过 internal ctor seam 注入 fake oldDir。
/// </summary>
public sealed class LocalDataMigrationServiceTests : IDisposable
{
    private readonly string _scratchRoot;
    private readonly string _fakeOldDir;

    public LocalDataMigrationServiceTests()
    {
        _scratchRoot = Path.Combine(
            Path.GetTempPath(), "local-data-migration-" + Guid.NewGuid().ToString("N"));
        _fakeOldDir = Path.Combine(_scratchRoot, "fake-old-appdata", "ComfyUI-Manager");
        Directory.CreateDirectory(_fakeOldDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private (LocalDataPaths paths, LocalDataMigrationService service) MakeService(string projectRoot)
    {
        var paths = new LocalDataPaths(projectRoot);
        var service = new LocalDataMigrationService(paths, logger: null, oldDirOverride: _fakeOldDir);
        return (paths, service);
    }

    private static string NewProjectRoot(string scratchRoot) =>
        Path.Combine(scratchRoot, "project-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RunIfNeeded_NoOldDir_ReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        Directory.Delete(_fakeOldDir, recursive: true); // 模拟 fresh machine: 旧目录不存在

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.False(ran);
        Assert.True(Directory.Exists(paths.Directory)); // .manager/ 仍被 LocalDataPaths ctor 建出来
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Directory));
    }

    [Fact]
    public void RunIfNeeded_OldDirHasFiles_NewDirEmpty_CopiesAndReturnsTrue()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeOldDir, "settings.json"), "{\"k\":1}");
        File.WriteAllText(Path.Combine(_fakeOldDir, "state.db"), "fake-db");
        File.WriteAllText(Path.Combine(_fakeOldDir, "release_cache.json"), "{}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "release_cache.json")));
        Assert.Equal("{\"k\":1}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
    }

    [Fact]
    public void RunIfNeeded_NewDirAlreadyHasFiles_SkipsAndReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, service) = MakeService(projectRoot);
        // Pre-populate .manager/ — 模拟"已迁移"或"用户已写入"
        File.WriteAllText(Path.Combine(paths.Directory, "settings.json"), "{\"existing\":true}");
        File.WriteAllText(Path.Combine(_fakeOldDir, "settings.json"), "{\"old\":true}");

        var ran = service.RunIfNeeded();

        Assert.False(ran); // idempotent:不重复迁移
        // 现有文件保持不变(不被覆盖)
        Assert.Equal("{\"existing\":true}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
    }

    [Fact]
    public void RunIfNeeded_BothDirsEmpty_ReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.False(ran);
        Assert.True(Directory.Exists(paths.Directory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Directory));
    }

    [Fact]
    public void RunIfNeeded_OldDirHasSubdirs_OnlyCopiesFiles()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeOldDir, "settings.json"), "{\"k\":1}");
        // 旧目录里可能有子目录 — plan 说"只复制文件,不递归目录"
        var subDir = Path.Combine(_fakeOldDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.json"), "{\"nested\":true}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        // 文件复制了
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        // 子目录 NOT 复制(plan: "OnlyCopiesFiles")
        Assert.False(Directory.Exists(Path.Combine(paths.Directory, "subdir")));
    }
}
