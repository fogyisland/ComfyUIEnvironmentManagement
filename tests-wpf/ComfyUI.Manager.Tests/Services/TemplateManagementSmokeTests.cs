using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// 防回归 smoke — TemplateManagementView 卡片用 <c>LocalDirBadgeHint = "本地目录为空"</c>
/// 红色 badge 提醒用户 clone 模板源码。GUI 手测容易漏看,改成程序化 smoke — 把 7 个内置
/// 模板逐一调 <c>LocalDirExists</c> + <c>LocalDirBadge</c>,跟实际磁盘对账:
///
/// <list type="bullet>
///   <item><b>已 shipped 3 个</b>(ComfyUI / Forge / OpenVoice)— 完整
///         git clone 产物,目录存在 + 非空 → <c>LocalDirExists = true</c> + <c>LocalDirBadge = ""</c></item>
///   <item><b>未 shipped 3 个</b>(Whisper / CoquiTTS / Bark)— 目录不存在 → <c>false</c>
///         + <c>"本地目录为空"</c> red badge 提示用户 clone</item>
/// </list>
///
/// <c>ENVTemplate/</c> 没 checkout 时整组测试 early-return(等同 skip),不破坏 CI;
/// shipped 状态变了(例如 OpenVoice 后续删了),手工更新 <see cref="ClonedBuiltinKinds"/> /
/// <see cref="PendingBuiltinKinds"/> 即可,不必重写测试。
///
/// v1.0.0.x (2026-08-29): SwarmUI 模板已下线 + 删除目录,总数 7 → 6。
/// v1.0.0.x: A1111 不再是内置模板(已下线),从所有列表移除,总数 8 → 7。
/// </summary>
public sealed class TemplateManagementSmokeTests
{
    // 仓库根 = 测试 dll 向上找含 ENVTemplate + src-wpf 的目录。
    // 不依赖环境变量 / SolutionDir,跨 dev/release/git-action 都能跑。
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string EnvTemplateAnchor =
        Path.Combine(RepoRoot, "ENVTemplate");

    /// <summary>
    /// shipped 状态下 <c>ENVTemplate/</c> 实际有内容的 3 个内置模板(完整 git clone 产物)。
    /// 测试断言 LocalDirExists=true 且 badge=""(不显示)。
    /// v1.0.0.x (2026-08-29): 从 4 个减到 3 个 — SwarmUI 已下线 + 目录已删除。
    /// v1.0.0.x: 从 5 个减到 4 个 — A1111 已下线。
    /// </summary>
    private static readonly string[] ClonedBuiltinKinds =
    {
        "ComfyUI", "Forge", "OpenVoice",
    };

    /// <summary>
    /// shipped 状态下 <c>ENVTemplate/</c> 不存在的 3 个内置模板(待 clone)。
    /// 测试断言 LocalDirExists=false 且 badge=<see cref="TemplateConfig.LocalDirBadgeHint"/>。
    /// </summary>
    private static readonly string[] PendingBuiltinKinds =
    {
        "Whisper", "CoquiTTS", "Bark",
    };

    /// <summary>
    /// 一站式枚举 6 个内置模板 → 实际 TemplateConfig 实例,供 [Theory] / [Fact] 用。
    /// 用 projectRoot="" 占位,LocalSourceDir 全部是 "<Kind>" 相对路径,不需要真 projectRoot。
    /// v1.0.0.x (2026-08-29): 从 7 个减到 6 个 — SwarmUI 已下线。
    /// </summary>
    private static IEnumerable<(string Kind, TemplateConfig Cfg)> AllBuiltins()
    {
        yield return ("ComfyUI",   TemplateConfigDefaults.ComfyUi(""));
        yield return ("Forge",     TemplateConfigDefaults.Forge(""));
        yield return ("OpenVoice", TemplateConfigDefaults.OpenVoice(""));
        yield return ("Whisper",   TemplateConfigDefaults.Whisper(""));
        yield return ("CoquiTTS",  TemplateConfigDefaults.CoquiTts(""));
        yield return ("Bark",      TemplateConfigDefaults.Bark(""));
    }

    /// <summary>
    /// 3 个已 shipped 内置模板,目录存在 + 非空 → badge 不显示,card 显示源 [本地]/[GitHub] 即可。
    /// </summary>
    [Fact]
    public void ClonedBuiltins_HaveLocalDir_NoBadge()
    {
        if (!Directory.Exists(EnvTemplateAnchor)) return; // ENVTemplate 未 shipped → skip

        foreach (var kind in ClonedBuiltinKinds)
        {
            var cfg = BuildCfg(kind);
            var resolved = TemplatePathResolver.Resolve(cfg.LocalSourceDir, EnvTemplateAnchor);
            Assert.True(Directory.Exists(resolved),
                $"{kind}: 期望已 shipped 但目录不存在:{resolved}");

            // shipped 状态下每个目录都来自 git clone,要么有 .git 要么非空
            var hasGit = Directory.Exists(Path.Combine(resolved, ".git"));
            var hasEntries = false;
            foreach (var _ in Directory.EnumerateFileSystemEntries(resolved))
            {
                hasEntries = true;
                break;
            }
            Assert.True(hasGit || hasEntries,
                $"{kind}: 目录存在但为空(疑似 partial clone):{resolved}");

            Assert.True(cfg.LocalDirExists(EnvTemplateAnchor),
                $"{kind}: LocalDirExists 返 false(实际目录已存在且非空):{resolved}");
            Assert.Equal("", cfg.LocalDirBadge(EnvTemplateAnchor));
        }
    }

    /// <summary>
    /// 3 个待 clone 内置模板,目录不存在 → badge 显示"本地目录为空",提醒用户。
    /// </summary>
    [Fact]
    public void PendingBuiltins_MissingDir_ShowRedBadge()
    {
        if (!Directory.Exists(EnvTemplateAnchor)) return; // ENVTemplate 未 shipped → skip

        foreach (var kind in PendingBuiltinKinds)
        {
            var cfg = BuildCfg(kind);
            var resolved = TemplatePathResolver.Resolve(cfg.LocalSourceDir, EnvTemplateAnchor);

            // 二次确认假设:目录确实不存在。如果有人手动 clone 了,
            // 测试失败要求更新 expected 列表(正常 — 说明 shipped 状态变了)。
            Assert.False(Directory.Exists(resolved),
                $"{kind}: 期望未 clone 但目录已存在:{resolved} — 请更新 PendingBuiltinKinds");

            Assert.False(cfg.LocalDirExists(EnvTemplateAnchor));
            Assert.Equal(TemplateConfig.LocalDirBadgeHint, cfg.LocalDirBadge(EnvTemplateAnchor));
            // LocalDirMissing 是 TemplateManagementViewModel 在构造 / Add / Edit 时
            // 写入的运行时标记,默认 false — VM 行为另由 ViewModel 测试覆盖。
        }
    }

    /// <summary>
    /// 防回归:TemplateConfigDefaults 必须正好注册 6 个内置模板(kind 列表 = Cloned + Pending)。
    /// 漏注册(用户报「只有 2 个模板」#497 历史)或重命名都会让这个测试 fail。
    /// 这个测试**不依赖** ENVTemplate/ 是否存在,锁的是代码契约。
    /// v1.0.0.x (2026-08-29): 从 7 个减到 6 个 — SwarmUI 已下线。
    /// v1.0.0.x: 从 8 个减到 7 个 — A1111 已下线。
    /// </summary>
    [Fact]
    public void AllBuiltins_EnumExactlySix()
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (kind, _) in AllBuiltins())
            actual.Add(kind);
        Assert.Equal(6, actual.Count);
        foreach (var kind in ClonedBuiltinKinds)
            Assert.Contains(kind, actual);
        foreach (var kind in PendingBuiltinKinds)
            Assert.Contains(kind, actual);
    }

    /// <summary>
    /// 防回归:6 个内置模板的 <see cref="TemplateConfig.CanDelete"/> 必须全部为 false(G13)。
    /// 任何内置模板漏写白名单 → 用户能删 → 模板管理列表掉项。
    /// </summary>
    [Fact]
    public void AllBuiltins_CannotBeDeleted()
    {
        foreach (var (kind, cfg) in AllBuiltins())
        {
            Assert.False(cfg.CanDelete, $"{kind}: 内置模板 CanDelete 必须 false");
        }
    }

    /// <summary>
    /// 防回归:内置模板 SourceKind + GitHubRepoUrl 配套。
    /// - Local 类(ComfyUI/Forge)— SourceKind=Local,无 repo URL 是 OK 的
    ///   (它们的 CanUpdateSource 走白名单,但 URL 是给 Update 用的,创建时不强制)。
    /// - GitHub 类(OpenVoice/Whisper/CoquiTTS/Bark)— SourceKind=GitHub + URL 非空,
    ///   才能被 TemplateSourceUpdater.CloneAsync 用上(否则 clone target 拿不到)。
    /// v1.0.0.x (2026-08-29): SwarmUI 已下线,从 Local case 列表移除。
    /// </summary>
    [Fact]
    public void AllBuiltins_SourceKindMatchesKind()
    {
        foreach (var (kind, cfg) in AllBuiltins())
        {
            switch (kind)
            {
                case "ComfyUI":
                case "Forge":
                    Assert.Equal(TemplateSourceKind.Local, cfg.SourceKind);
                    break;
                case "OpenVoice":
                case "Whisper":
                case "CoquiTTS":
                case "Bark":
                    Assert.Equal(TemplateSourceKind.GitHub, cfg.SourceKind);
                    Assert.False(string.IsNullOrWhiteSpace(cfg.GitHubRepoUrl),
                        $"{kind}: GitHub source 必须有 repo URL");
                    Assert.StartsWith("https://github.com/", cfg.GitHubRepoUrl);
                    break;
            }
        }
    }

    private static TemplateConfig BuildCfg(string kind) => kind switch
    {
        "ComfyUI"   => TemplateConfigDefaults.ComfyUi(""),
        "Forge"     => TemplateConfigDefaults.Forge(""),
        "OpenVoice" => TemplateConfigDefaults.OpenVoice(""),
        "Whisper"   => TemplateConfigDefaults.Whisper(""),
        "CoquiTTS"  => TemplateConfigDefaults.CoquiTts(""),
        "Bark"      => TemplateConfigDefaults.Bark(""),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// 找仓库根 = 向上找含 <c>ENVTemplate</c> + <c>src-wpf</c> 的目录。
    /// test dll 在 tests-wpf/.../bin/Debug/net8.0-windows/ 下,向上 5 层到 repo root。
    /// </summary>
    private static string LocateRepoRoot()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(probe); i++)
        {
            if (Directory.Exists(Path.Combine(probe, "ENVTemplate"))
                && Directory.Exists(Path.Combine(probe, "src-wpf")))
            {
                return probe;
            }
            var parent = Directory.GetParent(probe);
            if (parent is null) break;
            probe = parent.FullName;
        }
        // 兜底:AppContext.BaseDirectory 向上若干层 — 期望到 repo root
        return AppContext.BaseDirectory;
    }
}