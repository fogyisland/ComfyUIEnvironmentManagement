using System;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01): 验证 <see cref="RequirementsInstaller.ResolveRequirementsCandidates"/>
/// 在 TemplateConfigSnapshot.RequirementsFile 配置 Fooocus / HunyuanVideo / CogVideoX
/// 后返回正确 candidate path。
///
/// ResolveRequirementsCandidates 是 internal static,同 assembly 可访问。
/// 测试通过 RequirementsInstaller.InstallAsync 间接走(用 fake installer 拦截
/// 路径)或直接反射访问 internal member。后者更直接,本文件用反射。
/// </summary>
public sealed class RequirementsInstallerResolveCandidatesTests : IDisposable
{
    private readonly string _envRoot;

    public RequirementsInstallerResolveCandidatesTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"reqcandidates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// 反射调用 internal static <c>ResolveRequirementsCandidates(Environment)</c>。
    /// </summary>
    private static System.Collections.Generic.IReadOnlyList<string> InvokeResolveCandidates(
        Environment env)
    {
        var method = typeof(RequirementsInstaller).GetMethod(
            "ResolveRequirementsCandidates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new System.InvalidOperationException("ResolveRequirementsCandidates not found");
        return (System.Collections.Generic.IReadOnlyList<string>)method.Invoke(null, new object[] { env })!;
    }

    private Environment SeedEnv(string name, string requirementsFile)
    {
        var env = new Environment
        {
            Id = name,
            Name = name,
            RootPath = Path.Combine(_envRoot, name),
            TemplateKind = name,
            // 注意:不设 ComfyuiSource — 测试只关心 RequirementsFile 项行为差异
            // (原 3 paths 跟 v1.0.0.x 第 4 path 的区别)。ComfyuiSource 行为由
            // EnvironmentListViewModelBaseEnvTests 等其它 test 覆盖。
            // v1.0.0.x (2026-09-01): TemplateConfigSnapshot 镜像 EnvCreatorService
            // CloneTemplateConfig JSON round-trip(Environment.cs:70-71)
            TemplateConfigSnapshot = new TemplateConfig
            {
                Kind = name,
                Name = name,
                LocalSourceDir = name,
                RequirementsFile = requirementsFile,
            },
        };
        Directory.CreateDirectory(env.RootPath);
        // 写一个 fake requirements 文件让 candidate 选中
        if (!string.IsNullOrWhiteSpace(requirementsFile))
            File.WriteAllText(Path.Combine(env.RootPath, requirementsFile), "# fake");
        return env;
    }

    [Fact]
    public void Fooocus_TemplateConfigRequirementsFile_AddsFourthCandidate()
    {
        // v1.0.0.x (2026-09-01): Fooocus factory RequirementsFile = "requirements_versions.txt"
        // → ResolveRequirementsCandidates 返 3 个 candidates(2 fallback paths +
        // 1 RequirementsFile 路径;ComfyuiSource 留空)
        var env = SeedEnv("Fooocus", "requirements_versions.txt");

        var candidates = InvokeResolveCandidates(env);

        Assert.Equal(3, candidates.Count);
        Assert.Contains(Path.Combine(env.RootPath, "requirements_versions.txt"), candidates);
    }

    [Fact]
    public void HunyuanVideo_RequirementsFile_Txt_AddsFourthCandidate()
    {
        var env = SeedEnv("HunyuanVideo", "requirements.txt");

        var candidates = InvokeResolveCandidates(env);

        Assert.Equal(3, candidates.Count);
        Assert.Contains(Path.Combine(env.RootPath, "requirements.txt"), candidates);
    }

    [Fact]
    public void CogVideoX_RequirementsFile_Txt_AddsFourthCandidate()
    {
        var env = SeedEnv("CogVideoX", "requirements.txt");

        var candidates = InvokeResolveCandidates(env);

        Assert.Equal(3, candidates.Count);
        Assert.Contains(Path.Combine(env.RootPath, "requirements.txt"), candidates);
    }

    [Fact]
    public void LTXVideo_EmptyRequirementsFile_DoesNotAddFourthCandidate()
    {
        // v1.0.0.x (2026-09-01): LTXVideo uv sync 处理依赖,RequirementsFile = ""
        // → ResolveRequirementsCandidates 不加第 4 candidate(行为跟之前一致)
        var env = SeedEnv("LTXVideo", "");

        var candidates = InvokeResolveCandidates(env);

        Assert.Equal(2, candidates.Count);  // 只 2 fallback paths(无 ComfyuiSource,无 RequirementsFile)
        // 2 个 candidates 都是 <RootPath>/ComfyUI/requirements.txt + <RootPath>/requirements.txt
        // 这两条 fallback path 本来就跟 TemplateKind 无关 — LTXVideo / Fooocus 等都会
        // 出现。DoesNotContain 检查没有意义,只验 count 就够。
    }

    [Fact]
    public void EmptyTemplateConfigSnapshot_DoesNotAddFourthCandidate()
    {
        // 老 env 没 TemplateConfigSnapshot(env 是 env-create 前 / DB 列加载失败) →
        // RequirementsFile null → 不加第 4 candidate。ComfyuiSource 也空(测试 env 不设)
        // → 总共 2 个 candidates(只 2 fallback paths,没 ComfyuiSource,没 RequirementsFile)。
        var env = new Environment
        {
            Id = "Fooocus",
            Name = "Fooocus",
            RootPath = Path.Combine(_envRoot, "Fooocus"),
            TemplateKind = "Fooocus",
            TemplateConfigSnapshot = null,
        };

        var candidates = InvokeResolveCandidates(env);

        Assert.Equal(2, candidates.Count);  // 只 2 fallback + 0 ComfyuiSource + 0 RequirementsFile
    }
}
