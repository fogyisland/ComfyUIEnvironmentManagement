using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class SettingsTests
{
    [Fact]
    public void DefaultPythonVersion_DefaultsTo310()
    {
        var s = new Settings();
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_RoundTripsViaJson()
    {
        var s = new Settings { DefaultPythonVersion = "3.11" };
        var json = JsonSerializer.Serialize(s);
        Assert.Contains("\"default_python_version\":\"3.11\"", json);
        var restored = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(restored);
        Assert.Equal("3.11", restored!.DefaultPythonVersion);
    }

    [Fact]
    public void DefaultPythonVersion_DefaultsWhenJsonMissing()
    {
        // 旧 settings.json 没有 default_python_version 字段 → 反序列化后仍是 "3.10"
        var oldJson = "{\"template_python_dir\":\"python\",\"template_comfyui_dir\":\"ComfyUI\"}";
        var restored = JsonSerializer.Deserialize<Settings>(oldJson);
        Assert.NotNull(restored);
        Assert.Equal("3.10", restored!.DefaultPythonVersion);
    }

    [Fact]
    public void PythonInterpreters_RoundTrip()
    {
        var s = new Settings
        {
            PythonInterpreters = new List<PythonInterpreter>
            {
                new() { Name = "py3.10", Path = "D:/python/3.10/python.exe" },
                new() { Name = "py3.11", Path = "D:/python/3.11/python.exe" },
            },
            ActivePythonInterpreterName = "py3.11",
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json);

        Assert.NotNull(back);
        Assert.Equal(2, back!.PythonInterpreters.Count);
        Assert.Equal("py3.10", back.PythonInterpreters[0].Name);
        Assert.Equal("D:/python/3.10/python.exe", back.PythonInterpreters[0].Path);
        Assert.Equal("py3.11", back.PythonInterpreters[1].Name);
        Assert.Equal("py3.11", back.ActivePythonInterpreterName);
    }

    [Fact]
    public void Migration_FirstLoadFromLegacyTemplatePythonDir_CreatesDefaultEntry()
    {
        // v0.6.5.6 hotfix:fixture 必须有真实 python.exe,否则 migration 跳过合成(避免死路径)
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-mig-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        var versionDir = Path.Combine(tempDir, "3.10");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "python.exe"), "fake");
        try
        {
            // 老 settings.json 绝对路径在 JSON 里是带分隔符的,跨平台 compat 用 forward slash
            var dirInJson = tempDir.Replace('\\', '/');
            var json = $$"""
            {
              "template_python_dir": "{{dirInJson}}",
              "default_python_version": "3.10",
              "github_token": "abc"
            }
            """;

            var s = JsonSerializer.Deserialize<Settings>(json)!;
            SettingsDefaults.Apply(s, AppContext.BaseDirectory);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("3.10", s.PythonInterpreters[0].Name);
            // JSON 里 forward slash,Path.Combine 走 backslash —— 都用 Path.GetFullPath 规范化再比较
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "3.10", "python.exe")),
                Path.GetFullPath(s.PythonInterpreters[0].Path));
            Assert.Equal("3.10", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Migration_NoOp_WhenPythonInterpretersNonEmpty()
    {
        // v0.6.5.6 hotfix:fixture 必须有真实 python.exe,否则 cleanup 把它当坏条目删掉
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-noop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var userPy = Path.Combine(tempDir, "user-py.exe");
        File.WriteAllText(userPy, "fake");
        try
        {
            var dirInJson = tempDir.Replace('\\', '/');
            var json = $$"""
            {
              "python_interpreters": [
                { "name": "user-added", "path": "{{dirInJson}}/user-py.exe" }
              ],
              "active_python_interpreter_name": "user-added",
              "template_python_dir": "{{dirInJson}}",
              "default_python_version": "3.10"
            }
            """;

            var s = JsonSerializer.Deserialize<Settings>(json)!;
            SettingsDefaults.Apply(s, AppContext.BaseDirectory);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("user-added", s.PythonInterpreters[0].Name);
            Assert.Equal("user-added", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
