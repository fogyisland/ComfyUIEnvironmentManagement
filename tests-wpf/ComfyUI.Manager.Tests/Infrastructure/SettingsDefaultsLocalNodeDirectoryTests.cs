using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsLocalNodeDirectoryTests
{
    // v0.6.5.9: 新 LocalNodeDirectory 字段,template-style 默认子目录名("local-nodes"),
    // 跟 TemplatePythonDir / TemplateComfyuiDir 同语义(包自带资源类,落到程序根下)。
    // v1.0.0:目录重构,默认子目录名 PascalCase → "LocalNodes"(旧名 "local-nodes" 自动迁移)。
    // 旧 settings.json 没这字段 → JSON 反序列化用字段默认值 "" → Apply 兜底填 "LocalNodes"。
    // MigrateOnly 逻辑对绝对路径在 projectRoot 下时转相对,跟其它 path 字段保持一致。

    [Fact]
    public void Settings_LocalNodeDirectory_DefaultsToRelativeSubdir()
    {
        var s = new Settings();
        SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
        Assert.Equal("LocalNodes", s.LocalNodeDirectory);
    }

    [Fact]
    public void Settings_LocalNodeDirectory_PersistsAcrossReload()
    {
        var s = new Settings { LocalNodeDirectory = @"D:\my-nodes" };
        var json = JsonSerializer.Serialize(s);
        var s2 = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal(@"D:\my-nodes", s2.LocalNodeDirectory);
    }

    [Fact]
    public void Settings_LocalNodeDirectory_AbsolutePathUnderProjectRoot_MigratesToRelative()
    {
        var s = new Settings { LocalNodeDirectory = @"C:\fake\root\LocalNodes" };
        SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
        Assert.Equal("LocalNodes", s.LocalNodeDirectory);
    }

    [Fact]
    public void Settings_LocalNodeDirectory_AbsolutePathOutsideProjectRoot_KeptAsIs()
    {
        var s = new Settings { LocalNodeDirectory = @"D:\elsewhere\nodes" };
        SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
        Assert.Equal(@"D:\elsewhere\nodes", s.LocalNodeDirectory);
    }

    [Fact]
    public void Settings_LocalNodeDirectory_LegacyLowercase_MigratesToPascalCase()
    {
        // v1.0.0:老 settings.json 写的 "local-nodes"(kebab-case)→ "LocalNodes"
        var s = new Settings { LocalNodeDirectory = "local-nodes" };
        SettingsDefaults.Apply(s, projectRoot: @"C:\fake\root");
        Assert.Equal("LocalNodes", s.LocalNodeDirectory);
    }
}