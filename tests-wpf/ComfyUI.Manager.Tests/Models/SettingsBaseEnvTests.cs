using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public sealed class SettingsBaseEnvTests
{
    [Fact]
    public void BaseEnv_DefaultsToNewConfig_WhenFieldMissingInJson()
    {
        // 模拟老 settings.json(没有 base_env 字段)
        var oldJson = "{\"language\":\"zh_CN\"}";
        var s = JsonSerializer.Deserialize<Settings>(oldJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(s);
        Assert.NotNull(s!.BaseEnv);
        Assert.Equal("cu118", s.BaseEnv.CudaVersion);
        Assert.Equal("stable", s.BaseEnv.TorchChannel);
        Assert.Equal(4, s.BaseEnv.Packages.Count);
        Assert.Contains("torch", s.BaseEnv.Packages);
    }

    [Fact]
    public void BaseEnv_RoundTripsThroughJson()
    {
        var s = new Settings();
        s.BaseEnv.CudaVersion = "cu121";
        s.BaseEnv.TorchChannel = "nightly";
        s.BaseEnv.Packages = new System.Collections.Generic.List<string> { "torch", "xformers" };
        s.BaseEnv.ExtraArgs = "--user";
        s.BaseEnv.CustomPipArgs = "install torch";

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<Settings>(json)!;
        Assert.Equal("cu121", back.BaseEnv.CudaVersion);
        Assert.Equal("nightly", back.BaseEnv.TorchChannel);
        Assert.Equal(new[] { "torch", "xformers" }, back.BaseEnv.Packages);
        Assert.Equal("--user", back.BaseEnv.ExtraArgs);
        Assert.Equal("install torch", back.BaseEnv.CustomPipArgs);
    }
}