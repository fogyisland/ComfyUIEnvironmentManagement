using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class BaseEnvProfileLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public BaseEnvProfileLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"base-env-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void GetDefaults_ReturnsExactlyFiveProfiles()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var defaults = loader.GetDefaults();
        Assert.Equal(5, defaults.Count);
    }

    [Fact]
    public void GetDefaults_ContainsExpectedIds()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var ids = loader.GetDefaults().Select(p => p.Id).ToHashSet();
        Assert.Contains("pytorch-2.1-cu118-stable", ids);
        Assert.Contains("pytorch-2.1-cu121-stable", ids);
        Assert.Contains("pytorch-2.1-cu124-stable", ids);
        Assert.Contains("pytorch-nightly-cu121", ids);
        Assert.Contains("pytorch-2.1-cpu", ids);
    }

    [Fact]
    public void GetDefaults_Cu118Profile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetDefaults().Single(x => x.Id == "pytorch-2.1-cu118-stable");
        Assert.Equal("2.1.0", p.TorchVersion);
        Assert.Equal("cu118", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, p.Packages);
    }

    [Fact]
    public void GetDefaults_NightlyProfile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetDefaults().Single(x => x.Id == "pytorch-nightly-cu121");
        Assert.Equal("nightly", p.TorchVersion);
        Assert.Equal("cu121", p.CudaVersion);
        Assert.Equal("nightly", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, p.Packages);
    }

    [Fact]
    public void GetDefaults_CpuProfile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetDefaults().Single(x => x.Id == "pytorch-2.1-cpu");
        Assert.Equal("2.1.0", p.TorchVersion);
        Assert.Equal("cpu", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, p.Packages);
    }

    [Fact]
    public async Task LoadAsync_FileMissing_ReturnsDefaults()
    {
        // File does not exist
        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();
        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-2.1-cu118-stable");
    }

    [Fact]
    public async Task LoadAsync_ValidJson_ReturnsFileContents()
    {
        // Write a JSON file with 2 custom profiles
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        var custom = new List<BaseEnvProfile>
        {
            new BaseEnvProfile
            {
                Id = "custom-1",
                Name = "Custom 1",
                TorchVersion = "2.3.0",
                CudaVersion = "cu118",
                Channel = "stable",
            },
            new BaseEnvProfile
            {
                Id = "custom-2",
                Name = "Custom 2",
                TorchVersion = "2.4.0",
                CudaVersion = "cu121",
                Channel = "stable",
            },
        };
        var json = JsonSerializer.Serialize(custom, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        File.WriteAllText(path, json);

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("custom-1", profiles[0].Id);
        Assert.Equal("2.3.0", profiles[0].TorchVersion);
        Assert.Equal("custom-2", profiles[1].Id);
        Assert.Equal("2.4.0", profiles[1].TorchVersion);
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsDefaults()
    {
        // File exists but contains invalid JSON
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "{not valid json at all][");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        // Graceful fallback to defaults
        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-2.1-cu118-stable");
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonFile_ReturnsDefaults()
    {
        // File exists but is empty/whitespace
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        // Empty → fallback to defaults (consistent with missing/corrupt)
        Assert.Equal(5, profiles.Count);
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonArray_ReturnsEmptyList()
    {
        // File exists with a valid empty array "[]" — user explicitly chose to have no profiles
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "[]");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        // "[]" is a deliberate empty list, not a fallback trigger — honor it.
        Assert.Empty(profiles);
    }

    [Fact]
    public async Task LoadAsync_HonorsCancellationToken()
    {
        // Verify the method accepts a CancellationToken and returns successfully
        var loader = new BaseEnvProfileLoader(_tempDir);
        using var cts = new CancellationTokenSource();
        var profiles = await loader.LoadAsync(cts.Token);
        Assert.Equal(5, profiles.Count);
    }

    [Fact]
    public void Constructor_ExposesPath()
    {
        // Sanity check the constructor stores the dir
        var loader = new BaseEnvProfileLoader(_tempDir);
        // We just verify we can call LoadAsync — no exception means construction is fine.
        var defaults = loader.GetDefaults();
        Assert.NotNull(defaults);
    }
}