using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x:SettingsDefaults.Apply 自动 seed PythonInterpreter 当 shipped
/// portable python 存在。验证「不覆盖用户已配」「相对路径写入」「active 同步」。
/// </summary>
public class SettingsDefaultsPythonAutoSeedTests
{
    /// <summary>造一个临时 projectRoot,内含 python/python.exe (或指定子目录) — 模拟 shipped portable python。</summary>
    private static string CreateProjectRootWithPython(string? subdir = "python")
    {
        var root = Path.Combine(Path.GetTempPath(), "cmgr-py-seed-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, subdir!));
        File.WriteAllText(Path.Combine(root, subdir!, "python.exe"), "fake");
        return root;
    }

    [Fact]
    public void Apply_EmptyInterpreters_PortablePythonExists_SeedsRelativePath()
    {
        // shipped python 在 projectRoot/python/ → seed "python/python.exe" 相对路径
        // 关键:TemplatePythonDir/DefaultPythonVersion 两个老字段必须为空 — 否则 legacy
        // migration 会先触发(用老字段路径合成 PythonInterpreter),挡住相对路径 seed。
        var root = CreateProjectRootWithPython("python");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = "",
                DefaultPythonVersion = "",
            };
            SettingsDefaults.Apply(s, root);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("python", s.PythonInterpreters[0].Name);
            Assert.Equal(Path.Combine("python", "python.exe"), s.PythonInterpreters[0].Path);
            Assert.Equal("python", s.ActivePythonInterpreterName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Apply_NoPortablePython_NoSeed()
    {
        // projectRoot 没 python/ 子目录 → 不 seed,留空让用户 Browse
        var root = Path.Combine(Path.GetTempPath(), "cmgr-py-none-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = "",
                DefaultPythonVersion = "",
            };
            SettingsDefaults.Apply(s, root);

            Assert.Empty(s.PythonInterpreters);
            Assert.Equal("", s.ActivePythonInterpreterName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Apply_ExistingInterpreter_NotOverwritten()
    {
        // G13 保护:用户已加至少一条 → 不 seed
        var root = CreateProjectRootWithPython("python");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = "",
                DefaultPythonVersion = "",
                PythonInterpreters = new()
                {
                    new PythonInterpreter { Name = "user-py", Path = "C:/custom/python.exe" },
                },
                ActivePythonInterpreterName = "user-py",
            };
            SettingsDefaults.Apply(s, root);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("user-py", s.PythonInterpreters[0].Name);
            Assert.Equal("C:/custom/python.exe", s.PythonInterpreters[0].Path);
            Assert.Equal("user-py", s.ActivePythonInterpreterName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Apply_LegacyFieldsNonEmpty_BlocksAutoSeed()
    {
        // 关键 safety net:用户 settings.json 有 TemplatePythonDir/DefaultPythonVersion 老字段
        // (即使 legacy migration 合成失败)→ 不 auto-seed shipped python,避免覆盖用户意图。
        // 模拟 legacy migration "neither layout exists" 场景。
        var root = CreateProjectRootWithPython("python");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = @"D:\some\custom\dir",   // 不存在,legacy migration 跳过
                DefaultPythonVersion = "3.10",
            };
            SettingsDefaults.Apply(s, root);

            Assert.Empty(s.PythonInterpreters);   // 既不 legacy 合成也不 auto-seed
        }
        finally { Directory.Delete(root, true); }
    }
}