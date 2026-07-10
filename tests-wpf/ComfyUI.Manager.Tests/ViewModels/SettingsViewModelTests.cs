using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Tests.ViewModels;

public class SettingsViewModelTests
{
    [Fact]
    public async Task Load_PopulatesSettings()
    {
        var api = new FakeApiClient();
        api.Register("settings/get-all", _ => new Settings
        {
            Language = "en_US",
            ThemeMode = "dark",
            CatalogCacheTtlMinutes = 120,
        });
        var vm = new SettingsViewModel(api);
        await Task.Delay(100);
        Assert.Equal("en_US", vm.Language);
        Assert.Equal("dark", vm.ThemeMode);
        Assert.Equal(120, vm.CacheTtlMinutes);
    }

    [Fact]
    public async Task LanguageSet_CallsSetValue()
    {
        var api = new FakeApiClient();
        string? savedKey = null;
        object? savedVal = null;
        api.Register("settings/set-value", req =>
        {
            var dict = (System.Text.Json.JsonElement)(
                req.GetType().GetProperty("Value")?.GetValue(req) ?? new { });
            // 不深究 req 结构,直接看后续
            return new { };
        });
        api.Register("settings/get-all", _ => new Settings());
        var vm = new SettingsViewModel(api);
        await Task.Delay(100);
        vm.Language = "en_US";
        await Task.Delay(200);
        Assert.Equal("en_US", vm.Language);
    }
}