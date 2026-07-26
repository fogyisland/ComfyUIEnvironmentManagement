using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class BaseEnvProfileLoaderTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>
    /// Representative pytorch.org HTML: stable regex fires on the
    /// <c>"latest_stable"</c> field in <c>pt_published_versions</c> and the
    /// nightly cuda.x regex fires on the flat <c>"cuda.x"</c> key inside
    /// <c>pt_version_map.nightly</c>.
    /// </summary>
    private const string SampleHtml = """
        <script>
        var pt_published_versions = {"preview,pip,linux,cuda.x,python":"pip3 install --pre torch torchvision --index-url https://download.pytorch.org/whl/nightly/cu126","stable,pip,linux,cuda.x,python":"pip3 install torch torchvision --index-url https://download.pytorch.org/whl/cu126","latest_stable":"2.13.0"};
        var pt_version_map = {"nightly":{"accnone":["cpu",""],"cuda.x":["cuda","12.6"],"cuda.y":["cuda","13.0"],"cuda.z":["cuda","13.2"]},"release":{"accnone":["cpu",""],"cuda.x":["cuda","12.6"],"cuda.y":["cuda","13.0"],"cuda.z":["cuda","13.2"]}};
        </script>
        """;

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

    // ----- HTTP fakes (HttpMessageHandler via Moq) -----

    private static HttpClient MockedHttpClient(
        string html,
        HttpStatusCode status = HttpStatusCode.OK,
        Action? onSend = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                onSend?.Invoke();
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = status,
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                });
            });
        return new HttpClient(handler.Object);
    }

    private string FreshCacheDir()
    {
        var dir = Path.Combine(_tempDir, $"cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCache(string cacheDir, PyTorchLiveVersions versions)
    {
        var path = Path.Combine(cacheDir, PyTorchVersionCache.FileName);
        var json = JsonSerializer.Serialize(versions, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        File.WriteAllText(path, json);
    }

    // ----- Hardcoded defaults (renamed from GetDefaults_*) -----

    [Fact]
    public void GetHardcodedDefaults_ReturnsExactlyFiveProfiles()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var defaults = loader.GetHardcodedDefaults();
        Assert.Equal(5, defaults.Count);
    }

    [Fact]
    public void GetHardcodedDefaults_ContainsExpectedIds()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var ids = loader.GetHardcodedDefaults().Select(p => p.Id).ToHashSet();
        Assert.Contains("pytorch-2.1-cu118-stable", ids);
        Assert.Contains("pytorch-2.1-cu121-stable", ids);
        Assert.Contains("pytorch-2.1-cu124-stable", ids);
        Assert.Contains("pytorch-nightly-cu121", ids);
        Assert.Contains("pytorch-2.1-cpu", ids);
    }

    [Fact]
    public void GetHardcodedDefaults_Cu118Profile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.Id == "pytorch-2.1-cu118-stable");
        Assert.Equal("2.1.0", p.TorchVersion);
        Assert.Equal("cu118", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, p.Packages);
    }

    [Fact]
    public void GetHardcodedDefaults_NightlyProfile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.Id == "pytorch-nightly-cu121");
        Assert.Equal("nightly", p.TorchVersion);
        Assert.Equal("cu121", p.CudaVersion);
        Assert.Equal("nightly", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, p.Packages);
    }

    [Fact]
    public void GetHardcodedDefaults_CpuProfile_HasExpectedFields()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.Id == "pytorch-2.1-cpu");
        Assert.Equal("2.1.0", p.TorchVersion);
        Assert.Equal("cpu", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, p.Packages);
    }

    // ----- LoadAsync -----

    [Fact]
    public async Task LoadAsync_FallsBackWhenFileMissing()
    {
        // File does not exist, no HttpClient → hardcoded defaults (5 profiles).
        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();
        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-2.1-cu118-stable");
    }

    [Fact]
    public async Task LoadAsync_ValidJson_ReturnsFileContents()
    {
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
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "{not valid json at all][");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        // Graceful fallback to hardcoded defaults (no HttpClient).
        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-2.1-cu118-stable");
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonFile_ReturnsDefaults()
    {
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        Assert.Equal(5, profiles.Count);
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonArray_ReturnsEmptyList()
    {
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "[]");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        Assert.Empty(profiles);
    }

    [Fact]
    public async Task LoadAsync_HonorsCancellationToken()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        using var cts = new CancellationTokenSource();
        var profiles = await loader.LoadAsync(cts.Token);
        Assert.Equal(5, profiles.Count);
    }

    [Fact]
    public void Constructor_ExposesPath()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var defaults = loader.GetHardcodedDefaults();
        Assert.NotNull(defaults);
    }

    // ----- GetLiveDefaults (live-fetch path) -----

    [Fact]
    public async Task GetLiveDefaults_UsesFetchedStableVersion()
    {
        var loader = new BaseEnvProfileLoader(_tempDir, FreshCacheDir(), MockedHttpClient(SampleHtml));

        var profiles = await loader.GetLiveDefaultsAsync();

        // All stable profiles carry the fetched "2.13.0"; only nightly stays "nightly".
        foreach (var p in profiles.Where(x => x.Channel == "stable"))
        {
            Assert.Equal("2.13.0", p.TorchVersion);
        }
        Assert.Equal("nightly", profiles.Single(x => x.Channel == "nightly").TorchVersion);
    }

    [Fact]
    public async Task GetLiveDefaults_GeneratesSixProfiles()
    {
        var loader = new BaseEnvProfileLoader(_tempDir, FreshCacheDir(), MockedHttpClient(SampleHtml));

        var profiles = await loader.GetLiveDefaultsAsync();

        Assert.Equal(6, profiles.Count);
        var cudas = profiles.Select(p => p.CudaVersion).ToList();
        Assert.Contains("cu118", cudas);
        Assert.Contains("cu121", cudas);
        Assert.Contains("cu124", cudas);
        Assert.Contains("cu126", cudas);
        Assert.Contains("cpu", cudas);
        Assert.Contains(profiles, p => p.Id == "pytorch-nightly-cu126");
        Assert.Contains(profiles, p => p.Id == "pytorch-2.13.0-cu126-stable");
    }

    [Fact]
    public async Task GetLiveDefaults_NightlyProfileKeepsLiteralNightly()
    {
        var loader = new BaseEnvProfileLoader(_tempDir, FreshCacheDir(), MockedHttpClient(SampleHtml));

        var profiles = await loader.GetLiveDefaultsAsync();

        var nightly = profiles.Single(x => x.Id == "pytorch-nightly-cu126");
        Assert.Equal("nightly", nightly.TorchVersion);
        Assert.Equal("nightly", nightly.Channel);
        Assert.Equal("cu126", nightly.CudaVersion);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, nightly.Packages);
    }

    [Fact]
    public async Task GetLiveDefaults_FallsBackOnFetcherReturnsNull()
    {
        // 404 → fetcher returns null → hardcoded 5 profiles (nightly cu121).
        var loader = new BaseEnvProfileLoader(
            _tempDir, FreshCacheDir(), MockedHttpClient("not found", HttpStatusCode.NotFound));

        var profiles = await loader.GetLiveDefaultsAsync();

        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-nightly-cu121");
        Assert.Contains(profiles, p => p.Id == "pytorch-2.1-cu118-stable");
    }

    [Fact]
    public async Task GetLiveDefaults_UsesCacheWhenFresh()
    {
        var cacheDir = FreshCacheDir();
        WriteCache(cacheDir, new PyTorchLiveVersions
        {
            Stable = "2.9.9",
            HasNightlyCu126 = true,
            FetchedAt = DateTimeOffset.UtcNow, // fresh
        });

        var httpCalls = 0;
        var loader = new BaseEnvProfileLoader(
            _tempDir, cacheDir, MockedHttpClient(SampleHtml, onSend: () => httpCalls++));

        var profiles = await loader.GetLiveDefaultsAsync();

        // Cache hit → HTTP never called → cached "2.9.9" used, not fetched "2.13.0".
        Assert.Equal(0, httpCalls);
        Assert.Equal(6, profiles.Count);
        Assert.All(profiles.Where(p => p.Channel == "stable"), p => Assert.Equal("2.9.9", p.TorchVersion));
    }

    [Fact]
    public async Task GetLiveDefaults_RefetchesWhenCacheExpired()
    {
        var cacheDir = FreshCacheDir();
        WriteCache(cacheDir, new PyTorchLiveVersions
        {
            Stable = "2.9.9",
            HasNightlyCu126 = true,
            FetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2), // expired (>1h TTL)
        });

        var httpCalls = 0;
        var loader = new BaseEnvProfileLoader(
            _tempDir, cacheDir, MockedHttpClient(SampleHtml, onSend: () => httpCalls++));

        var profiles = await loader.GetLiveDefaultsAsync();

        // Expired cache → HTTP called → fresh "2.13.0" used, not stale "2.9.9".
        Assert.Equal(1, httpCalls);
        Assert.All(profiles.Where(p => p.Channel == "stable"), p => Assert.Equal("2.13.0", p.TorchVersion));
    }

    [Fact]
    public async Task GetLiveDefaults_WithoutHttp_UsesHardcoded()
    {
        // No HttpClient / cacheDir → hardcoded defaults, no network.
        var loader = new BaseEnvProfileLoader(_tempDir);

        var profiles = await loader.GetLiveDefaultsAsync();

        Assert.Equal(5, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-nightly-cu121");
    }

    // ----- LoadProfilesForVersionAsync (Task 4: per-version generation) -----

    [Fact]
    public async Task LoadProfilesForVersion_StableUsesSelectedVersionAndVariants()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var metadata = new PyTorchVersion
        {
            Version = "2.5.1",
            CudaVariants = new[] { "cu118", "cu121", "cu124", "cu126" },
            HasCpu = true,
        };

        var profiles = await loader.LoadProfilesForVersionAsync("2.5.1", metadata);

        Assert.All(profiles.Where(p => p.Channel == "stable"), p => Assert.Equal("2.5.1", p.TorchVersion));
        var cudaTags = profiles.Where(p => p.CudaVersion != "cpu").Select(p => p.CudaVersion).ToList();
        Assert.Contains("cu118", cudaTags);
        Assert.Contains("cu121", cudaTags);
        Assert.Contains("cu124", cudaTags);
        Assert.Contains("cu126", cudaTags);
        Assert.Contains("cpu", profiles.Select(p => p.CudaVersion));
        Assert.DoesNotContain(profiles, p => p.Channel == "nightly");
    }

    [Fact]
    public async Task LoadProfilesForVersion_NightlyProducesSingleCu126Profile()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);

        var profiles = await loader.LoadProfilesForVersionAsync("nightly");

        Assert.Single(profiles);
        Assert.Equal("cu126", profiles[0].CudaVersion);
        Assert.Equal("nightly", profiles[0].TorchVersion);
        Assert.Equal("nightly", profiles[0].Channel);
    }

    [Fact]
    public async Task LoadProfilesForVersion_NoMetadataForStable_ReturnsLiveOrFallback()
    {
        // No metadata passed → loader derives profiles from live defaults (or hardcoded fallback)
        // and filters to TorchVersion == "2.5.1".
        var loader = new BaseEnvProfileLoader(_tempDir);

        var profiles = await loader.LoadProfilesForVersionAsync("2.5.1");

        // Stable profiles carry the requested version.
        Assert.NotEmpty(profiles);
        Assert.All(profiles.Where(p => p.Channel == "stable"), p => Assert.Equal("2.5.1", p.TorchVersion));
    }

    [Fact]
    public async Task LoadProfilesForVersion_CpuOnlyMetadata_NoCudaProfile()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var metadata = new PyTorchVersion
        {
            Version = "2.5.1",
            CudaVariants = Array.Empty<string>(),
            HasCpu = true,
        };

        var profiles = await loader.LoadProfilesForVersionAsync("2.5.1", metadata);

        Assert.Single(profiles);
        Assert.Equal("cpu", profiles[0].CudaVersion);
        Assert.Equal("stable", profiles[0].Channel);
        Assert.Equal("2.5.1", profiles[0].TorchVersion);
    }

    [Fact]
    public async Task LoadProfilesForVersion_PreservesPackageList()
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var metadata = new PyTorchVersion
        {
            Version = "2.5.1",
            CudaVariants = new[] { "cu118" },
            HasCpu = true,
        };

        var stableProfiles = await loader.LoadProfilesForVersionAsync("2.5.1", metadata);
        var stableCuda = stableProfiles.Single(p => p.CudaVersion == "cu118");
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, stableCuda.Packages);

        var cpu = stableProfiles.Single(p => p.CudaVersion == "cpu");
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, cpu.Packages);

        var nightly = await loader.LoadProfilesForVersionAsync("nightly");
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, nightly[0].Packages);
    }

    [Fact]
    public async Task LoadAsync_JsonFileUnchangedBehavior()
    {
        // Regression: custom JSON file with 2 profiles → LoadAsync returns them unchanged.
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        var custom = new List<BaseEnvProfile>
        {
            new BaseEnvProfile
            {
                Id = "custom-a",
                Name = "Custom A",
                TorchVersion = "2.7.0",
                CudaVersion = "cu118",
                Channel = "stable",
            },
            new BaseEnvProfile
            {
                Id = "custom-b",
                Name = "Custom B",
                TorchVersion = "2.8.0",
                CudaVersion = "cpu",
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
        Assert.Equal("custom-a", profiles[0].Id);
        Assert.Equal("custom-b", profiles[1].Id);
    }
}
