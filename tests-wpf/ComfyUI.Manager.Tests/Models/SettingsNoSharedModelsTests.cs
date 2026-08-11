using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v0.6.11+ SDD B T2:SharedModelsDirectory 字段从 model 移除。模型上根本没有这个属性;
/// 启动时 _repo.Load() 静默忽略 disk 上遗留的 shared_models_directory JSON 字段
/// (原生 JsonSerializer 默认)。
/// </summary>
public sealed class SettingsNoSharedModelsTests
{
    [Fact]
    public void Settings_DoesNotExposeSharedModelsDirectory()
    {
        // 编译期就不该有这个 property;这条 assertion 主要是为了 reviewer grep
        // 一眼能验。如果有人误加回 SharedModelsDirectory property,这条会编译失败
        // —— 删它。
        Assert.False(typeof(Settings).GetProperty("SharedModelsDirectory") is not null,
            "SharedModelsDirectory property 应已从 Settings model 删除");
    }

    [Fact]
    public void JsonLoad_IgnoresLegacySharedModelsDirectoryField()
    {
        // 模拟老 disk:写一份含 shared_models_directory 的 JSON
        var legacyJson = """{"shared_models_directory":"D:\\legacy","default_models_directory":"D:\\default"}""";
        var s = JsonSerializer.Deserialize<Settings>(legacyJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(s);
        // legacy 字段被忽略,不影响 default 字段读取
        Assert.Equal("D:\\default", s!.DefaultModelsDirectory);
    }
}
