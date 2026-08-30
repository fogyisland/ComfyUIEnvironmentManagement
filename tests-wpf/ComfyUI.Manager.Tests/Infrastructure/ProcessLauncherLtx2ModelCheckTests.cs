using System;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v1.0.0.x (2026-08-30) LTX-2 T7:
/// - <see cref="ProcessLauncher.BuildStartCommand"/> 新增 <c>{models}</c> / <c>{env}</c> 占位符
///   (LTX-2 走 CLI 模式,EntryArgs 要显式拼 5 个权重绝对路径 + 输出路径)。
/// - <see cref="ProcessLauncher.EnsureLtx2ModelsPresent"/> 启动前检查 5 个 .safetensors。
/// </summary>
public sealed class ProcessLauncherLtx2ModelCheckTests : IDisposable
{
    private readonly string _projectRoot;

    public ProcessLauncherLtx2ModelCheckTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), "ltx2-t7-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// BuildStartCommand 会校验入口脚本存在性(Spec §9),所以每个 case 必须真建一个假
    /// entry 文件 —— 否则先抛 InvalidOperationException,断言根本拿不到 args。
    /// 镜像 ProcessLauncherTemplateKindTests.CreateFakeEntryFile 的既有 pattern。
    /// </summary>
    private string MakeEnvRoot(string envName, string entryScript)
    {
        var root = Path.Combine(_projectRoot, "envs", envName);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, entryScript), "@echo off");
        return root;
    }

    private Environment MakeLtx2Env(string modelsDir)
    {
        var root = MakeEnvRoot("ltx-test", "run-ltx2-distilled.bat");
        return new Environment
        {
            Id = "test-id",
            Name = "ltx-test",
            RootPath = root,
            TemplateKind = "LTXVideo",
            ModelsDirectory = modelsDir,
            Port = 8188,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "LTXVideo",
                Name = "LTXVideo",
                // T1 rollback 后 built-in LocalSourceDir 是 "LTXVideo"(brand-name
                // "LTX-Video" 已撤回)。本 test 不走 source 解析路径,该字段只是占位。
                LocalSourceDir = "LTXVideo",
                EntryScript = "run-ltx2-distilled.bat",
                // 真实模板 EntryArgs 更长(5 个 --*-path + --output-path),这里精简成
                // 一个 {models} + 一个 {env} 足够覆盖替换逻辑。
                EntryArgs = "--transformer-path {models}/ltx-2.5/x.safetensors --out {env}/o.mp4",
            },
        };
    }

    [Fact]
    public void BuildStartCommand_Replaces_Models_And_Env_Placeholders()
    {
        var env = MakeLtx2Env("D:/models");
        var settings = new Settings();

        var (_, (_, args)) = ProcessLauncher.BuildStartCommand(env, settings, _projectRoot);

        // 纯字符串替换:EntryArgs 里的 "/" 分隔符原样保留(Windows 接受正斜杠)。
        Assert.Contains("--transformer-path D:/models/ltx-2.5/x.safetensors", args);
        Assert.Contains($"--out {env.RootPath}/o.mp4", args);
        Assert.DoesNotContain("{models}", args);
        Assert.DoesNotContain("{env}", args);
    }

    [Fact]
    public void BuildStartCommand_PortPlaceholder_StillWorks()
    {
        var root = MakeEnvRoot("port-x", "main.py");
        var env = new Environment
        {
            Id = "x",
            Name = "x",
            RootPath = root,
            TemplateKind = "ComfyUI",
            ModelsDirectory = "",
            Port = 9090,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                Name = "ComfyUI",
                LocalSourceDir = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--port {port}",
            },
        };

        var (_, (_, args)) = ProcessLauncher.BuildStartCommand(env, new Settings(), _projectRoot);

        Assert.Contains("--port 9090", args);
    }

    [Fact]
    public void BuildStartCommand_MissingModelsPlaceholder_LeavesArgEmpty()
    {
        var root = MakeEnvRoot("no-models", "main.py");
        var env = new Environment
        {
            Id = "x",
            Name = "x",
            RootPath = root,
            TemplateKind = "ComfyUI",
            ModelsDirectory = "",
            Port = 9090,
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = "ComfyUI",
                Name = "ComfyUI",
                LocalSourceDir = "ComfyUI",
                EntryScript = "main.py",
                EntryArgs = "--ckpt {models}/c.pt",
            },
        };

        var (_, (_, args)) = ProcessLauncher.BuildStartCommand(env, new Settings(), _projectRoot);

        // ModelsDirectory 空 → {models} 替换为空串,生成 "--ckpt /c.pt" 但不 throw。
        Assert.Contains("--ckpt", args);
        Assert.DoesNotContain("{models}", args);
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_AllExist_DoesNotThrow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltx-models-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // touch 5 个文件
            foreach (var p in new Environment
                     {
                         TemplateKind = "LTXVideo",
                         ModelsDirectory = dir,
                     }.Ltx2RequiredModels)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[] { 1 });
            }

            var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = dir };
            ProcessLauncher.EnsureLtx2ModelsPresent(env);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_Missing_ThrowsModelsMissingException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltx-models-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var env = new Environment { TemplateKind = "LTXVideo", ModelsDirectory = dir };
            var ex = Assert.Throws<ModelsMissingException>(
                () => ProcessLauncher.EnsureLtx2ModelsPresent(env));
            Assert.Equal(5, ex.MissingPaths.Count);
            Assert.Contains("huggingface.co/Lightricks/LTX-2.5", ex.HuggingFaceRepoUrl);
            Assert.Contains("hf download Lightricks/LTX-2.5", ex.DownloadCommand);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnsureLtx2ModelsPresent_NonLTXVideo_DoesNotCheck()
    {
        var env = new Environment { TemplateKind = "ComfyUI", ModelsDirectory = "" };
        ProcessLauncher.EnsureLtx2ModelsPresent(env);  // 不抛
    }
}
