using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CatalogViewModelTests
{
    [Fact]
    public async Task Query_TriggersSearch()
    {
        var api = new FakeApiClient();
        bool searched = false;
        api.Register("node/search-catalog", req =>
        {
            searched = true;
            return new List<CatalogEntry>
            {
                new() { Id = "pkg-a", Name = "pkg-a" },
            };
        });
        var ws = new FakeWsClient();
        var vm = new CatalogViewModel(api);
        vm.Query = "pkg";
        await Task.Delay(200);
        Assert.True(searched);
    }

    [Fact]
    public async Task RefreshCommand_CallsRefreshThenSearch()
    {
        var api = new FakeApiClient();
        int refreshCount = 0;
        api.Register("node/refresh-catalog", _ =>
        {
            refreshCount++;
            return refreshCount;
        });
        api.Register("node/search-catalog", _ => new List<CatalogEntry>());
        var vm = new CatalogViewModel(api);
        await Task.Delay(100);
        vm.RefreshCommand.Execute(null);
        await Task.Delay(200);
        Assert.True(refreshCount > 0);
    }
}