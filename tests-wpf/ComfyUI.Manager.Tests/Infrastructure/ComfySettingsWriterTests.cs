using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.7.2:ComfySettingsWriter 行为测试 —— 启动 env 前写
/// <c>&lt;comfyui-root&gt;/user/default/comfy.settings.json</c> 的 Comfy.Locale 字段,
/// 保留其它已有 key。模拟文件不存在、文件存在但空、文件有其它字段、
/// Comfy.Locale 已被 ComfyUI 自己写过、文件 JSON 损坏 5 种场景。
/// </summary>
public sealed class ComfySettingsWriterTests : IDisposable
{
    private readonly string _root;

    public ComfySettingsWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "comfysettings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SettingsPath => Path.Combine(_root, "user", "default", "comfy.settings.json");

    private static Dictionary<string, JsonElement> ReadBack(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
    }

    [Fact]
    public void WriteLocale_NoExistingFile_CreatesWithLocaleOnly()
    {
        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "zh");

        Assert.True(File.Exists(SettingsPath));
        var dict = ReadBack(SettingsPath);
        Assert.Single(dict);
        Assert.Equal("zh", dict["Comfy.Locale"].GetString());
    }

    [Fact]
    public void WriteLocale_ExistingOtherKeys_PreservesThem()
    {
        // ComfyUI 自己写的 settings(其它字段存在)
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              "Comfy.ColorPalette": "dark",
              "Comfy.Sidebar.Location": "left"
            }
            """);

        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "zh");

        var dict = ReadBack(SettingsPath);
        Assert.Equal(3, dict.Count);
        Assert.Equal("zh", dict["Comfy.Locale"].GetString());
        Assert.Equal("dark", dict["Comfy.ColorPalette"].GetString());
        Assert.Equal("left", dict["Comfy.Sidebar.Location"].GetString());
    }

    [Fact]
    public void WriteLocale_ExistingLocale_OverwritesOnlyThatKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              "Comfy.Locale": "en",
              "Comfy.ColorPalette": "dark"
            }
            """);

        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "ja");

        var dict = ReadBack(SettingsPath);
        Assert.Equal(2, dict.Count);
        Assert.Equal("ja", dict["Comfy.Locale"].GetString());
        Assert.Equal("dark", dict["Comfy.ColorPalette"].GetString());
    }

    [Fact]
    public void WriteLocale_ExistingEmptyFile_WritesFresh()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "");

        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "fr");

        var dict = ReadBack(SettingsPath);
        Assert.Single(dict);
        Assert.Equal("fr", dict["Comfy.Locale"].GetString());
    }

    [Fact]
    public void WriteLocale_MalformedExistingFile_WritesFreshWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{not valid json");

        var writer = new ComfySettingsWriter();
        var ex = Record.Exception(() => writer.WriteLocale(_root, "ko"));

        Assert.Null(ex);
        var dict = ReadBack(SettingsPath);
        Assert.Single(dict);
        Assert.Equal("ko", dict["Comfy.Locale"].GetString());
    }

    [Fact]
    public void WriteLocale_EmptyLocaleString_NoOp()
    {
        // 之前可能存在的旧 settings 应当原封不动
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var original = """
            {
              "Comfy.ColorPalette": "dark"
            }
            """;
        File.WriteAllText(SettingsPath, original);

        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "");

        Assert.Equal(original.Trim(), File.ReadAllText(SettingsPath).Trim());
    }

    [Fact]
    public void WriteLocale_CreatesUserDefaultDirWhenMissing()
    {
        // _root 存在但 user/default/ 完全不存在
        Assert.False(Directory.Exists(Path.Combine(_root, "user")));

        var writer = new ComfySettingsWriter();
        writer.WriteLocale(_root, "zh");

        Assert.True(Directory.Exists(Path.Combine(_root, "user", "default")));
        Assert.True(File.Exists(SettingsPath));
    }
}