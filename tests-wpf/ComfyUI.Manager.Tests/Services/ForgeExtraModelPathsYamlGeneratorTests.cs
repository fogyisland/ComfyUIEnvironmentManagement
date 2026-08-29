using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-08-29):Forge extra_model_paths.yaml 自动生成器测试。
///
/// 覆盖 <see cref="ForgeExtraModelPathsYamlGenerator.BuildYamlContent"/>
/// (纯函数)和 <see cref="ForgeExtraModelPathsYamlGenerator.EnsureWritten"/>
/// (副作用函数)的 9 个 boundary case。
/// </summary>
public class ForgeExtraModelPathsYamlGeneratorTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "cmgr-forge-yaml-" + Guid.NewGuid().ToString("N")[..8]);

    public ForgeExtraModelPathsYamlGeneratorTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void Build_DefaultModelsDirSet_WritesBasePathAndAllSubkeys()
    {
        // 核心 case:DefaultModelsDirectory 非空 → YAML 含 base_path + 6 subdir 字段。
        // 用 forward-slash 路径避免 Path.Combine 返 backslash(测试断言基于 YAML 文本
        // 字面比对,而生成器对 base_path 已经做了 ToForwardSlash)。
        var settings = new Settings { DefaultModelsDirectory = "D:/models" };

        var yaml = ForgeExtraModelPathsYamlGenerator.BuildYamlContent(settings);

        Assert.NotEmpty(yaml);
        // 顶层 section key + base_path
        Assert.Contains($"{ForgeExtraModelPathsYamlGenerator.SectionKey}:", yaml);
        Assert.Contains("base_path: D:/models", yaml);
        // 6 个 ComfyUI 风格子目录字段 + 对应路径
        Assert.Contains("checkpoints: D:/models/checkpoints", yaml);
        Assert.Contains("loras: D:/models/loras", yaml);
        Assert.Contains("vae: D:/models/vae", yaml);
        Assert.Contains("embeddings: D:/models/embeddings", yaml);
        Assert.Contains("hypernetworks: D:/models/hypernetworks", yaml);
        Assert.Contains("controlnet: D:/models/controlnet", yaml);
    }

    [Fact]
    public void Build_DefaultModelsDirEmpty_ReturnsEmptyString()
    {
        // 空串 = 用户没配 DefaultModelsDirectory → BuildYamlContent 返 "",
        // caller(EnsureWritten)抛 InvalidOperationException 拒绝写文件。
        var settings = new Settings { DefaultModelsDirectory = "" };

        var yaml = ForgeExtraModelPathsYamlGenerator.BuildYamlContent(settings);

        Assert.Equal("", yaml);
    }

    [Fact]
    public void Build_DefaultModelsDirWhitespace_ReturnsEmptyString()
    {
        // 全空白(用户误填 "   " 或 "\t\n")→ 跟空串同语义,返 ""。
        var settings = new Settings { DefaultModelsDirectory = "   \t  " };

        var yaml = ForgeExtraModelPathsYamlGenerator.BuildYamlContent(settings);

        Assert.Equal("", yaml);
    }

    [Fact]
    public void Build_BackslashPath_ConvertedToForwardSlash()
    {
        // Windows 用户在 Settings 配 "D:\models"(settings.ini JSON 里写成
        // "D:\\models")→ 生成器必须把 backslash 转 forward slash,A1111/Forge yaml
        // 惯例 + 跨平台 yaml parser 友好。
        var settings = new Settings { DefaultModelsDirectory = @"D:\models" };

        var yaml = ForgeExtraModelPathsYamlGenerator.BuildYamlContent(settings);

        Assert.Contains("base_path: D:/models", yaml);
        Assert.Contains("checkpoints: D:/models/checkpoints", yaml);
        // 不能残留 backslash(会影响 yaml 解析:PyYAML 在某些 context 把 "\D" 视为
        // 转义序列前缀)。
        Assert.DoesNotContain(@"D:\models", yaml.Replace("base_path: D:/models", ""));
    }

    [Fact]
    public void Build_PathWithTrailingSlash_Handled()
    {
        // 用户在 Settings 配 "D:/models/"(末尾带斜杠)→ Path.GetFullPath
        // 已经会 normalize 掉尾斜杠,base_path 不带尾斜杠 + 6 subdir 派生正常。
        // 测试锁住"末尾斜杠不影响输出"行为,避免后续重构时回归。
        var settings = new Settings { DefaultModelsDirectory = "D:/models/" };

        var yaml = ForgeExtraModelPathsYamlGenerator.BuildYamlContent(settings);

        Assert.Contains("base_path: D:/models", yaml);
        Assert.Contains("checkpoints: D:/models/checkpoints", yaml);
    }

    [Fact]
    public void EnsureWritten_NonExistentFile_Writes()
    {
        // 目标 yaml 文件不存在 → EnsureWritten 创建 envRootPath + yaml。
        var envRoot = Path.Combine(_workDir, "ForgeA");
        var settings = new Settings { DefaultModelsDirectory = "D:/models" };

        ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings);

        var yamlPath = Path.Combine(envRoot, "extra_model_paths.yaml");
        Assert.True(Directory.Exists(envRoot), "envRoot must be created");
        Assert.True(File.Exists(yamlPath), "yaml file must exist");
        var content = File.ReadAllText(yamlPath);
        Assert.Contains($"{ForgeExtraModelPathsYamlGenerator.SectionKey}:", content);
        Assert.Contains("checkpoints: D:/models/checkpoints", content);
    }

    [Fact]
    public void EnsureWritten_ExistingFile_Overwrites()
    {
        // 已有 yaml 文件(老内容)→ EnsureWritten 覆盖写。锁住"幂等"行为,
        // 让 EnvCreator step 7.5 + ProcessLauncher 启动前双写不会出错。
        var envRoot = Path.Combine(_workDir, "ForgeB");
        Directory.CreateDirectory(envRoot);
        var yamlPath = Path.Combine(envRoot, "extra_model_paths.yaml");
        File.WriteAllText(yamlPath, "stale content from old schema\n");

        var settings = new Settings { DefaultModelsDirectory = "D:/models" };
        ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings);
        // 再调一次,验证幂等(内容不变)。
        ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings);

        var content = File.ReadAllText(yamlPath);
        Assert.DoesNotContain("stale content", content);
        Assert.Contains($"{ForgeExtraModelPathsYamlGenerator.SectionKey}:", content);
    }

    [Fact]
    public void EnsureWritten_EmptyDefaultModelsDir_Throws()
    {
        // DefaultModelsDirectory 为空时 EnsureWritten 必须抛
        // InvalidOperationException(不静默写一个空 base_path 的 yaml —
        // 那会被 Forge 解析成 "D:/" 这种 root,更糟糕)。
        var envRoot = Path.Combine(_workDir, "ForgeC");
        Directory.CreateDirectory(envRoot);
        var settings = new Settings { DefaultModelsDirectory = "" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings));

        Assert.Contains("DefaultModelsDirectory", ex.Message);
        // 失败时也不应残留部分 yaml 文件(原子写:tmp → move;失败 throw,
        // tmp 被 catch-all 清理)。
        Assert.False(File.Exists(Path.Combine(envRoot, "extra_model_paths.yaml")));
    }

    [Fact]
    public void EnsureWritten_WritesAtEnvRoot()
    {
        // 锁住 yaml 文件路径 = <envRootPath>/extra_model_paths.yaml
        // (不能写到 cwd 或 settings 路径,Forge webui.py 启动时只查 cwd)。
        var envRoot = Path.Combine(_workDir, "ForgeD", "nested", "deep");
        var settings = new Settings { DefaultModelsDirectory = "D:/models" };

        ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings);

        var expectedPath = Path.Combine(envRoot, "extra_model_paths.yaml");
        Assert.True(File.Exists(expectedPath),
            $"yaml must be written at <envRoot>/extra_model_paths.yaml = {expectedPath}");
        // 同步覆盖 env 嵌套目录创建(EnsureWritten 应递归创建)。
        Assert.True(Directory.Exists(envRoot));
    }

    [Fact]
    public void EnsureWritten_ExistingFileWithUserSection_LogsWarningAndOverwrites()
    {
        // 用户在 yaml 里手写了一个 section(我们的生成器之外),按 design 覆盖 +
        // Debug.WriteLine 警告。测试通过:文件被覆盖,不 crash。
        var envRoot = Path.Combine(_workDir, "ForgeE");
        Directory.CreateDirectory(envRoot);
        var yamlPath = Path.Combine(envRoot, "extra_model_paths.yaml");
        File.WriteAllText(yamlPath,
            "my_user_section:\n  custom_field: custom_value\n");

        var settings = new Settings { DefaultModelsDirectory = "D:/models" };
        ForgeExtraModelPathsYamlGenerator.EnsureWritten(envRoot, settings);

        var content = File.ReadAllText(yamlPath);
        // 用户 section 被覆盖(当前 design 不 preserve,YAGNI)。
        Assert.DoesNotContain("my_user_section", content);
        // 新生成的内容到位。
        Assert.Contains(ForgeExtraModelPathsYamlGenerator.SectionKey, content);
    }
}