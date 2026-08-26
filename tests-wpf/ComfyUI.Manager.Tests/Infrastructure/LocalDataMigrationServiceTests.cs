using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// 验证一次性数据迁移 service —— 把 .manager/(v1.0.0.x 之前的本地数据) 和
/// %APPDATA%/ComfyUI-Manager/(v0.6.16 之前的远古目录) 都合并到 &lt;projectRoot&gt;/config/。
///
/// 每个 test 用唯一 temp dir 模拟旧 + 新目录,避免污染真实 APPDATA。
/// 通过 internal ctor seam 注入 fake 旧目录路径。
/// </summary>
public sealed class LocalDataMigrationServiceTests : IDisposable
{
    private readonly string _scratchRoot;
    private readonly string _fakeAppDataDir;
    private readonly string _fakeLegacyManagerDir;

    public LocalDataMigrationServiceTests()
    {
        _scratchRoot = Path.Combine(
            Path.GetTempPath(), "local-data-migration-" + Guid.NewGuid().ToString("N"));
        _fakeAppDataDir = Path.Combine(_scratchRoot, "fake-appdata", "ComfyUI-Manager");
        // legacyManagerDir 在 projectRoot/config/ 旁边 — 模拟 <projectRoot>/.manager/
        // 测试用 scratchRoot 作为 projectRoot 的父目录,_fakeLegacyManagerDir 放 scratchRoot/.manager/
        _fakeLegacyManagerDir = Path.Combine(_scratchRoot, ".manager");
        Directory.CreateDirectory(_fakeAppDataDir);
        Directory.CreateDirectory(_fakeLegacyManagerDir);
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
        var service = new LocalDataMigrationService(
            paths,
            logger: null,
            appDataOldDir: _fakeAppDataDir,
            legacyManagerDir: _fakeLegacyManagerDir);
        return (paths, service);
    }

    private static string NewProjectRoot(string scratchRoot) =>
        Path.Combine(scratchRoot, "project-" + Guid.NewGuid().ToString("N"));

    // ============== v0.6.16 APPDATA 迁移测试(legacy) ==============

    [Fact]
    public void RunIfNeeded_NoOldDirs_ReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        Directory.Delete(_fakeAppDataDir, recursive: true);
        Directory.Delete(_fakeLegacyManagerDir, recursive: true);

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.False(ran);
        Assert.True(Directory.Exists(paths.Directory)); // config/ 仍被 LocalDataPaths ctor 建出来
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Directory));
    }

    [Fact]
    public void RunIfNeeded_AppDataHasFiles_NewDirEmpty_CopiesAndReturnsTrue()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"k\":1}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "state.db"), "fake-db");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.Equal("{\"k\":1}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
        // APPDATA 源目录保留(legacy less-destructive)
        Assert.True(Directory.Exists(_fakeAppDataDir));
    }

    [Fact]
    public void RunIfNeeded_NewDirAlreadyHasFiles_SkipsAndReturnsFalse()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        var (paths, service) = MakeService(projectRoot);
        // Pre-populate config/ — 模拟"已迁移"或"用户已写入"
        File.WriteAllText(Path.Combine(paths.Directory, "settings.json"), "{\"existing\":true}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"old\":true}");

        var ran = service.RunIfNeeded();

        Assert.False(ran); // idempotent:不重复迁移
        Assert.Equal("{\"existing\":true}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
    }

    [Fact]
    public void RunIfNeeded_AppDataHasSubdirs_OnlyCopiesFiles()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"k\":1}");
        var subDir = Path.Combine(_fakeAppDataDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.json"), "{\"nested\":true}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.Directory, "subdir")));
    }

    // ============== v1.0.0.x #569 .manager/ → config/ 迁移测试 ==============

    [Fact]
    public void RunIfNeeded_LegacyManagerHasFiles_NewDirEmpty_CopiesAndDeletesSource()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        // 模拟 v1.0.0.x 之前的 install:.manager/ 在 projectRoot 旁,有 state.db + cache
        // projectRoot = scratchRoot/project-X/,所以 .manager/ 应该在 scratchRoot/.manager/
        // 我们的 _fakeLegacyManagerDir 已经指向 scratchRoot/.manager/(跟 projectRoot 同级)
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "state.db"), "fake-state-db");
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "release_cache.json"), "{}");
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "pytorch_catalog_cache.json"), "{}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "release_cache.json")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "pytorch_catalog_cache.json")));
        Assert.Equal("fake-state-db", File.ReadAllText(Path.Combine(paths.Directory, "state.db")));
        // 合并完成后 .manager/ 被删除(用户要求"全并入 config/",不再保留)
        Assert.False(Directory.Exists(_fakeLegacyManagerDir));
    }

    [Fact]
    public void RunIfNeeded_BothLegacyManagerAndAppDataPresent_LegacyManagerWins()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        // 两段源都有文件;.manager/ 是更新数据,优先迁它;APPDATA 跳过
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "settings.json"), "{\"from_manager\":true}");
        File.WriteAllText(Path.Combine(_fakeAppDataDir, "settings.json"), "{\"from_appdata\":true}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        // .manager/ 段先跑,迁移完 config/ 不空 → APPDATA 段跳过
        Assert.Equal("{\"from_manager\":true}", File.ReadAllText(Path.Combine(paths.Directory, "settings.json")));
        // .manager/ 已被删除
        Assert.False(Directory.Exists(_fakeLegacyManagerDir));
        // APPDATA 保留(legacy less-destructive)
        Assert.True(Directory.Exists(_fakeAppDataDir));
    }

    [Fact]
    public void RunIfNeeded_LegacyManagerHasSubdirs_OnlyCopiesFiles()
    {
        var projectRoot = NewProjectRoot(_scratchRoot);
        File.WriteAllText(Path.Combine(_fakeLegacyManagerDir, "state.db"), "fake");
        var subDir = Path.Combine(_fakeLegacyManagerDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.json"), "{}");

        var (paths, service) = MakeService(projectRoot);

        var ran = service.RunIfNeeded();

        Assert.True(ran);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "state.db")));
        Assert.False(Directory.Exists(Path.Combine(paths.Directory, "subdir")));
    }
}