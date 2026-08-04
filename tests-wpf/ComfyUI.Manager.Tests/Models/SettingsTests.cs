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
        var json = """
        {
          "template_python_dir": "D:/python",
          "default_python_version": "3.10",
          "github_token": "abc"
        }
        """;

        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);

        Assert.Single(s.PythonInterpreters);
        Assert.Equal("3.10", s.PythonInterpreters[0].Name);
        Assert.Equal(Path.Combine("D:/python", "3.10", "python.exe"), s.PythonInterpreters[0].Path);
        Assert.Equal("3.10", s.ActivePythonInterpreterName);
        // 老字段保留
        Assert.Equal("D:/python", s.TemplatePythonDir);
        Assert.Equal("3.10", s.DefaultPythonVersion);
    }

    [Fact]
    public void Migration_NoOp_WhenPythonInterpretersNonEmpty()
    {
        var json = """
        {
          "python_interpreters": [
            { "name": "user-added", "path": "E:/custom/python.exe" }
          ],
          "active_python_interpreter_name": "user-added",
          "template_python_dir": "D:/python",
          "default_python_version": "3.10"
        }
        """;

        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        SettingsDefaults.Apply(s, AppContext.BaseDirectory);

        Assert.Single(s.PythonInterpreters);
        Assert.Equal("user-added", s.PythonInterpreters[0].Name);
        Assert.Equal("user-added", s.ActivePythonInterpreterName);
    }
}
