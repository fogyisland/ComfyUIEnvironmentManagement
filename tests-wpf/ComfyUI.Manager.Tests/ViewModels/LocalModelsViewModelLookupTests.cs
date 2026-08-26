using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0 T11:Lookup command canExecute + IsLookupEnabled/IsLookupInProgress 计算属性测试。
/// 构造 LocalModelCard + 真 CivitAiLookupService(HttpMessageHandler mock 喂真 service,
/// 同 CivitAiLookupServiceTests 模式 — CivitAiLookupService 是 sealed,Moq 不能 mock)。
/// 我们只测 canExecute / IsLookupEnabled 这些不触发真 HTTP 的 guard 行为,所以 mock 的
/// response body 不重要。
/// </summary>
public sealed class LocalModelsViewModelLookupTests
{
    private static Settings SettingsWith(string modelsDir) => new() { DefaultModelsDirectory = modelsDir };

    private static LocalModelCard LocalCard(string title = "animatelora") => new(
        SourceId: "local:lora/" + title,
        Title: title,
        Kind: ModelKind.LORA,
        Source: "Local",
        VersionCount: 1,
        LatestDownloadedAt: DateTime.Now,
        SourceUrl: null,
        PreviewImagePath: null,
        Hash: null,
        MatchedDetail: null,
        MatchSource: null);

    private static LocalModelCard CivitAiCard(string title = "downloaded-model") => new(
        SourceId: "civitai:12345",
        Title: title,
        Kind: ModelKind.LORA,
        Source: ModelSourceKind.CivitAi.ToString(),
        VersionCount: 1,
        LatestDownloadedAt: DateTime.Now,
        SourceUrl: "https://civitai.com/models/12345",
        PreviewImagePath: null,
        Hash: null,
        MatchedDetail: null,
        MatchSource: null);

    /// <summary>构造一个 HttpClient + 真 CivitAiLookupService。Service 通过 HttpClient 解耦,
    /// canExecute 不发 HTTP 所以 response 内容无关紧要。</summary>
    private static CivitAiLookupService BuildLookupService()
    {
        var http = new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri("https://civitai.com/"),
        };
        return new CivitAiLookupService(http, "https://civitai.com", "");
    }

    [Fact]
    public void LookupCommand_LocalSourceCard_CanExecuteTrue()
    {
        // v1.0.0.x: LookupCommand 走 SelectedCard — 先 set 再 CanExecute(null)。
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());
        vm.SelectedCard = LocalCard();

        Assert.True(vm.LookupCivitAiCommand.CanExecute(null));
    }

    [Fact]
    public void LookupCommand_NonLocalSourceCard_CanExecuteFalse()
    {
        // v1.0.0.x: 选 Source != "Local" 的卡 → 按钮 disable(原 inline 按钮路径)。
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());
        vm.SelectedCard = CivitAiCard();

        Assert.False(vm.LookupCivitAiCommand.CanExecute(null));
    }

    [Fact]
    public void LookupCommand_NullLookupService_CanExecuteFalse()
    {
        // lookup = null → 即便 Source="Local" 也禁用(button 也会被 CardSourceVisibility 隐藏)
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: null);
        vm.SelectedCard = LocalCard();

        Assert.False(vm.LookupCivitAiCommand.CanExecute(null));
    }

    [Fact]
    public void IsLookupEnabled_LocalCardWithLookup_ReturnsTrue()
    {
        // v1.0.0.x: IsLookupEnabled(card) 是 inline 按钮用的辅助函数,toolbar 走
        // IsLookupEnabledForSelectedCard。两条路径都保留 — inline 按钮已删,但 helper API
        // 不删(测试 + 未来可能再启用 inline)。assertion 仍按旧 contract 验。
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());

        Assert.True(vm.IsLookupEnabled(LocalCard()));
    }

    [Fact]
    public void IsLookupEnabled_NonLocalCard_ReturnsFalse()
    {
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());

        Assert.False(vm.IsLookupEnabled(CivitAiCard()));
    }

    [Fact]
    public void IsLookupEnabled_LocalCardNoLookup_ReturnsFalse()
    {
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: null);

        Assert.False(vm.IsLookupEnabled(LocalCard()));
    }

    [Fact]
    public void LookupCommand_InitialState_IsAvailableForLocalCard()
    {
        // 验证 in-flight 集合初始为空,IsLookupInProgress 守卫初始未触发。
        // 真实"双击防抖"靠 GUI smoke 验证(在 UI 线程 ShowDialog block 期间
        // CanExecute 受 IsLookupInProgress 守卫)。
        // v1.0.0.x: 走 SelectedCard — 设 SelectedCard = LocalCard → CanExecute(null) = true。
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());
        var card = LocalCard();
        vm.SelectedCard = card;

        Assert.True(vm.LookupCivitAiCommand.CanExecute(null));
        Assert.False(vm.IsLookupInProgress(card));
    }

    // ===== v1.0.0 T13-7:Pre-matched card lookup wired through VM =====

    [Fact]
    public void LookupCommand_PreMatchedCard_CanExecuteTrue()
    {
        // Pre-matched card (MatchedDetail 非 null) 仍然 Source="Local" → LookupCommand 仍可执行。
        // Dialog VM 在 ctor 检测到 card.MatchedDetail 非 null → 直接开 Detail state,跳过 Searching。
        // (verified separately in LocalModelCivitAiDialogViewModelTests.Ctor_PreMatchedDetail_OpensDirectlyInDetailState)
        // v1.0.0.x: 走 SelectedCard。
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: BuildLookupService());
        var card = new LocalModelCard(
            SourceId: "local:99",
            Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
            LatestDownloadedAt: DateTime.Now, SourceUrl: null, PreviewImagePath: null,
            Hash: "ABCDEF",
            MatchedDetail: new CivitAiDetailDto(99, "Test Model", "u", null, "desc",
                Array.Empty<string>(), Array.Empty<CivitAiVersionDto>(), Array.Empty<string>()),
            MatchSource: MatchSource.Hash);
        vm.SelectedCard = card;

        Assert.True(vm.LookupCivitAiCommand.CanExecute(null));
        Assert.True(vm.IsLookupEnabled(card));
    }

    [Fact]
    public void LookupCommand_HashMatcherInOrchestrator_DoesNotThrow()
    {
        // 验证 orchestrator 集成的 service (9-arg ctor) 注入到 VM 时,LookupCommand 仍可执行。
        // 真实匹配走 service.MatchAsync (orchestrator 决定 4 策略顺序);本测试只验 wiring 不抛。
        // v1.0.0.x: 走 SelectedCard。
        var svc = new CivitAiLookupService(new System.Net.Http.HttpClient(), "https://civitai.com", "");
        var vm = new LocalModelsViewModel(SettingsWith("Z:\\fake"), new FakeScanner(),
            lookup: svc);
        var card = new LocalModelCard(
            SourceId: "local:1",
            Title: "Test", Kind: ModelKind.Checkpoint, Source: "Local", VersionCount: 1,
            LatestDownloadedAt: DateTime.Now, SourceUrl: null, PreviewImagePath: null,
            Hash: null, MatchedDetail: null, MatchSource: null);
        vm.SelectedCard = card;

        Assert.NotNull(vm.LookupCivitAiCommand);
        Assert.True(vm.LookupCivitAiCommand.CanExecute(null));
    }
}
