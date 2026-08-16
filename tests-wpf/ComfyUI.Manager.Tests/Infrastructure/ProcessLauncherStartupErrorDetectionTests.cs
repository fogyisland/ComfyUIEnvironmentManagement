using System.Collections.Generic;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.15.7 T6: 验证 ProcessLauncher 集成 NodeStartupErrorDetector 的关键部件。
///
/// 不真起 ComfyUI — ProcessLauncher.StartEnvAsync 需要 python + main.py + 端口,
/// 单测成本太高。这里只验证 detector 本身(ProcessLauncher 集成通过端到端 GUI smoke 验)。
/// detector 单测已存在,这里补几个跟 ProcessLauncher 接线相关的语义:
/// 1. detector 拿 lines → 出 NodeStartupError list (PackageName + ErrorMessage)
/// 2. 空输入 → 空 list
/// 3. 同 PackageName 多次出现 → dedup by first occurrence(ProcessLauncher 拿
///    StartupLines snapshot 喂 detector 时会去重)
///
/// v0.6.15.7 T9:验证 Get → null 的场景 GetByPackageName fallback 能命中真实行。
/// import error 报的是 package name(directory 内的 __init__.py 元数据),跟 env 装行 id
/// (dir name,跟 package 一致时才行)不一定对得上 — 实际项目里 package 名跟 dir 名常不等
/// (例如 "comfyui-impact-pack" vs "ComfyUI-Impact-Pack")。fallback 测试聚焦
/// 「行确实存在,GetByPackageName 能找」,ProcessLauncher 集成由 wiring 单测覆盖。
/// </summary>
public class ProcessLauncherStartupErrorDetectionTests
{
    [Fact]
    public void Detector_ParseCalled_ReturnsExpectedErrorList()
    {
        // 不真起 ComfyUI — 单测 detector + 验证 NodeStartupErrorDetector 拼装
        var detector = new NodeStartupErrorDetector();
        var lines = new[] {
            "Failed to import module 'comfyui-impact-pack'",
            "ModuleNotFoundError: No module named 'openai'",
        };
        var errors = detector.Parse(lines);
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.PackageName == "comfyui-impact-pack");
        Assert.Contains(errors, e => e.PackageName == "openai");
    }

    [Fact]
    public void Detector_EmptyLines_ReturnsEmpty()
    {
        var detector = new NodeStartupErrorDetector();
        var errors = detector.Parse(new string[0]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Detector_DuplicatePackageNames_DedupesByFirstOccurrence()
    {
        var detector = new NodeStartupErrorDetector();
        var lines = new[] {
            "Failed to import module 'pkg-x'",
            "ModuleNotFoundError: No module named 'pkg-x'",
        };
        var errors = detector.Parse(lines);
        Assert.Single(errors);
        Assert.Equal("pkg-x", errors[0].PackageName);
        Assert.Contains("Failed to import module", errors[0].ErrorMessage);  // first wins
    }

    [Fact]
    public void NodeRepository_FallbackByPackageName_FindsRowAfterIdLookupFails()
    {
        // v0.6.15.7 T9:模拟 import error 命中场景
        //   行:Id="ImpactPack", Package="comfyui-impact-pack"(dir name ≠ package name)
        //   detector 报 PackageName="comfyui-impact-pack"
        //   _nodeRepo.Get("comfyui-impact-pack") → null(id 不匹配)
        //   _nodeRepo.GetByPackageName(envId, "comfyui-impact-pack") → 行(走 fallback)
        using var db = new TestDb();
        var envRepo = new EnvironmentRepository(db.Factory);
        envRepo.Upsert(new ComfyUI.Manager.Models.Environment
        {
            Id = "env-x",
            Name = "env-x",
            RootPath = "/x/env-x",
            ComfyuiLayout = "standalone",
        });
        var repo = new NodeRepository(db.Factory);
        repo.Upsert(new ScannedNode
        {
            Id = "ImpactPack",
            EnvId = "env-x",
            Package = "comfyui-impact-pack",
            PackagePath = "/x/env-x/cust-nodes/ComfyUI-Impact-Pack",
            Source = "env",
        });

        Assert.Null(repo.Get("comfyui-impact-pack"));                   // id lookup fails
        var byPackage = repo.GetByPackageName("env-x", "comfyui-impact-pack");
        Assert.NotNull(byPackage);
        Assert.Equal("ImpactPack", byPackage!.Id);
        Assert.Equal("comfyui-impact-pack", byPackage.Package);
    }
}