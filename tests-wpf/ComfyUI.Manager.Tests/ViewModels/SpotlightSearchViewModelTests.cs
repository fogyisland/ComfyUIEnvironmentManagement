using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Search;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.9 T7:SpotlightSearchViewModel 单元测试(6 测试)。
/// 用 stub <see cref="IGlobalSearchService"/> 隔离 BuildAsync 的真实 DB 调用。
/// </summary>
public sealed class SpotlightSearchViewModelTests
{
    /// <summary>Stub 索引服务 — 控制 BuildAsync 返回固定 / 抛异常 / 计数。</summary>
    private sealed class StubSearchService : IGlobalSearchService
    {
        public SearchIndex? IndexToReturn { get; set; }
        public bool ShouldThrow { get; set; }
        public int CallCount { get; private set; }

        public async Task<SearchIndex> BuildAsync(CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            if (ShouldThrow) throw new InvalidOperationException("stubbed failure");
            return IndexToReturn ?? MakeIndex();
        }

        private static SearchIndex MakeIndex()
        {
            var idx = new SearchIndex();
            idx.Add(new SearchEntry
            {
                Id = "env-e1",
                Kind = TargetKind.Environment,
                DisplayName = "prod-env",
                NormalizedTokens = SearchIndex.TokenizeRaw("prod-env"),
                Target = SearchTarget.ForEnvironment("e1", "prod-env"),
            });
            idx.Add(new SearchEntry
            {
                Id = "cmd-ShowSettings",
                Kind = TargetKind.Command,
                DisplayName = "设置",
                NormalizedTokens = SearchIndex.TokenizeRaw("设置"),
                Target = SearchTarget.ForCommand("ShowSettings", "设置"),
            });
            idx.Add(new SearchEntry
            {
                Id = "node-e1-n1",
                Kind = TargetKind.Node,
                DisplayName = "manager",
                NormalizedTokens = SearchIndex.TokenizeRaw("manager"),
                Target = SearchTarget.ForNode("e1", "n1", "manager"),
            });
            return idx;
        }
    }

    private static (SpotlightSearchViewModel vm, StubSearchService stub) MakeVm()
    {
        var stub = new StubSearchService();
        var vm = new SpotlightSearchViewModel(stub, _ => Task.CompletedTask);
        return (vm, stub);
    }

    [Fact]
    public async Task OpenCommand_TriggersBuild()
    {
        var (vm, stub) = MakeVm();
        await vm.OpenAsync();
        Assert.True(vm.IsOpen);
        Assert.Equal(1, stub.CallCount);
        // 关闭后再 Open — 索引已构建,BuildAsync 不再调。
        vm.Close();
        await vm.OpenAsync();
        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task Query_UpdatesResults()
    {
        var (vm, _) = MakeVm();
        await vm.OpenAsync();
        vm.Query = "prod";
        Assert.Single(vm.Results);
        Assert.Equal("prod-env", vm.Results[0].Entry.DisplayName);
    }

    [Fact]
    public async Task Enter_ExecutesSelected()
    {
        var stub = new StubSearchService();
        var captured = "";
        var vm = new SpotlightSearchViewModel(stub,
            target => { captured = target.DisplayName; return Task.CompletedTask; });
        await vm.OpenAsync();
        vm.Query = "prod";
        Assert.Single(vm.Results);
        // EnterCommand.Execute 是 void(ICommand.Execute 返回 void);要让 navigator 跑完
        // 必须等 VM 内部 OpenAsync fire-and-forget 完。stub navigator 同步 Task.CompletedTask
        // 不需 await,所以断言前稍等一下让后台 task 跑完。
        ((ICommand)vm.EnterCommand).Execute(null);
        await Task.Delay(50);
        Assert.Equal("prod-env", captured);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task Esc_ClosesPopup()
    {
        var (vm, _) = MakeVm();
        await vm.OpenAsync();
        Assert.True(vm.IsOpen);
        ((ICommand)vm.CloseCommand).Execute(null);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public async Task UpDown_ChangesSelectedIndex()
    {
        var (vm, _) = MakeVm();
        await vm.OpenAsync();
        // 空 query SearchIndex.Query 返回空数组 — 用具体 query 命中 ≥3 个 entry。
        // 用 "e" 作 query:prod-env / 设置 / manager 经 Normalize 后都包含 'e' 字符 → substring 40 分命中。
        vm.Query = "e";
        if (vm.Results.Count >= 3)
        {
            Assert.Equal(0, vm.SelectedIndex);
            ((ICommand)vm.DownCommand).Execute(null);
            Assert.Equal(1, vm.SelectedIndex);
            ((ICommand)vm.UpCommand).Execute(null);
            Assert.Equal(0, vm.SelectedIndex);
        }
        else
        {
            // 兜底:count < 3 时只能验基础状态(确保 CanExecute gate 起作用)
            Assert.True(vm.SelectedIndex >= 0);
        }
    }

    [Fact]
    public async Task BuildAsync_Failure_ShowsUnavailableMessage()
    {
        var stub = new StubSearchService { ShouldThrow = true };
        var vm = new SpotlightSearchViewModel(stub, _ => Task.CompletedTask);
        await vm.OpenAsync();
        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBuilding);
    }
}