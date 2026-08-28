using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v0.6.12:Settings.LogDirectory 序列化 + 默认值 + SettingsDefaults.Apply 行为。
/// </summary>
public class SettingsLogDirectoryTests
{
    [Fact]
    public void Settings_LogDirectory_DefaultsToEmpty()
    {
        var settings = new Settings();
        Assert.Equal("", settings.LogDirectory);
    }

    [Fact]
    public void Settings_LogDirectory_RoundtripsThroughJson()
    {
        var original = new Settings { LogDirectory = @"D:\my-logs" };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<Settings>(json);
        Assert.Equal(@"D:\my-logs", restored.LogDirectory);
    }

    [Fact]
    public void SettingsDefaults_Apply_EmptyLogDirectory_SeedsAbsoluteLogsPath()
    {
        // v1.0.0.x 用户原话"日志目录也列出绝对路径,目录为 logs" ——
        // LogDirectory 改 ResolveAsAbsolute,空 → seed 当前 projectRoot + "logs"
        // 的绝对路径(<projectRoot>/logs),跟 EnvsDir / LocalNodesDirectory 等
        // 本地资源路径一致。绝对路径之外的字段(已填的)走 MigrateOnly 保留原值。
        var settings = new Settings { LogDirectory = "" };
        SettingsDefaults.Apply(settings, projectRoot: @"C:\fake-root");
        Assert.Equal(@"C:\fake-root\logs", settings.LogDirectory);
    }

    [Fact]
    public void SettingsDefaults_Apply_CreatesDirectoryIfNotExists()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"settings-logdir-{Guid.NewGuid():N}");
        try
        {
            var settings = new Settings { LogDirectory = tmpDir };
            SettingsDefaults.Apply(settings, projectRoot: @"C:\fake-root");
            Assert.True(Directory.Exists(tmpDir), $"Expected {tmpDir} created");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
