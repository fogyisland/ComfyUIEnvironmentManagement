using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Inf;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-08-31):settings.inf → Settings → Templates["Fooocus"].FooocusEntryMode
/// round-trip 测试 — 验证手编辑 settings.inf 把 fooocus_entry_mode = Stable 写入后,
/// 重新读回 Settings 时正确反序列化为 FooocusEntryMode.Stable(InfSettingsSerializer 反射
/// + JsonStringEnumConverter 路径)。
/// </summary>
public sealed class SettingsInfRoundTripFooocusTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsInfRoundTripFooocusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "settings-inf-fooocus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SettingsInf_FooocusEntryMode_Stable_RoundTripPreservesValue()
    {
        // 写一个 settings 含 Fooocus 模板 + fooocus_entry_mode = Stable 到 INF
        var settings = new Settings();
        settings.Templates["Fooocus"] = new TemplateConfig
        {
            Name = "Fooocus",
            Kind = "Fooocus",
            LocalSourceDir = "Fooocus",
            SourceKind = TemplateSourceKind.GitHub,
            GitHubRepoUrl = "https://github.com/lllyasviel/Fooocus.git",
            EntryScript = "entry_with_update.py",
            EntryArgs = "--port {port} --listen",
            ModelsSubdir = "models",
            ExtraJunctionTargets = new System.Collections.Generic.List<string>(),
            UserExtraArgs = "",
            FooocusEntryMode = FooocusEntryMode.Stable,
        };
        var dict = InfSettingsSerializer.SerializeToDict(settings);

        // 反向应用 dict 到新 Settings 实例
        var restored = new Settings();
        InfSettingsSerializer.ApplyDictToSettings(restored, dict);

        // 关键断言:settings.Templates["Fooocus"].FooocusEntryMode = Stable(用户手编辑生效)
        Assert.True(restored.Templates.ContainsKey("Fooocus"));
        Assert.Equal(FooocusEntryMode.Stable, restored.Templates["Fooocus"].FooocusEntryMode);
    }
}
