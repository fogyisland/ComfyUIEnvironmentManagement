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
    public void GetHardcodedDefaults_ReturnsExactlySevenProfiles()
    {
        // v0.6.5.18: hardcoded 路径生成 5 stable (cu118/121/124/126/128) + 1 nightly cu121 + 1 cpu
        var loader = new BaseEnvProfileLoader(_tempDir);
        var defaults = loader.GetHardcodedDefaults();
        Assert.Equal(7, defaults.Count);
    }

    [Fact]
    public void GetHardcodedDefaults_ContainsExpectedIds()
    {
        // v0.6.5.22 (Fix Round 1): torch 升 2.4.1,Id 是 pytorch-2.4.1-...;
        //   MarkIncompatibleOlderVersions 只改 Name 不改 Id,所以这里用精确匹配。
        //   (所有 stable profile TorchVersion >= 2.4,Name 不带后缀,Id 也干净。)
        var loader = new BaseEnvProfileLoader(_tempDir);
        var ids = loader.GetHardcodedDefaults().Select(p => p.Id).ToList();
        Assert.Contains("pytorch-2.4.1-cu118-stable", ids);
        Assert.Contains("pytorch-2.4.1-cu121-stable", ids);
        Assert.Contains("pytorch-2.4.1-cu124-stable", ids);
        Assert.Contains("pytorch-2.4.1-cu126-stable", ids);
        Assert.Contains("pytorch-2.4.1-cu128-stable", ids);
        Assert.Contains("pytorch-nightly-cu121", ids);
        Assert.Contains("pytorch-2.4.1-cpu", ids);
    }

    [Fact]
    public void GetHardcodedDefaults_Cu128Profile_HasExpectedFields()
    {
        // v0.6.5.18: cu128 加进 hardcoded defaults(fallback 路径也得有)
        // v0.6.5.22 (Fix Round 1): TorchVersion 从 2.1.0 升到 2.4.1
        //   (comfy_kitchen 兼容)。Id 改用 CudaVersion 定位
        //   (MarkIncompatibleOlderVersions 改 Name 不改 Id,但 Name 现在带后缀)。
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.CudaVersion == "cu128" && x.Channel == "stable");
        Assert.Equal("2.4.1", p.TorchVersion);
        Assert.Equal("cu128", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, p.Packages);
    }

    [Fact]
    public void GetHardcodedDefaults_FirstProfileIsCu118_ForBackwardCompat()
    {
        // 历史默认一直是 cu118(v0.6.5 之前),保留第一项让已有 env 的 BED 列还能
        // 显示,用户手动重选才能改 CUDA。
        var loader = new BaseEnvProfileLoader(_tempDir);
        var first = loader.GetHardcodedDefaults()[0];
        Assert.Equal("cu118", first.CudaVersion);
    }

    [Fact]
    public void GetHardcodedDefaults_Cu118Profile_HasExpectedFields()
    {
        // v0.6.5.22 (Fix Round 1): TorchVersion 升 2.4.1
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.CudaVersion == "cu118" && x.Channel == "stable");
        Assert.Equal("2.4.1", p.TorchVersion);
        Assert.Equal("cu118", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision", "xformers" }, p.Packages);
    }

    [Fact]
    public void GetHardcodedDefaults_NightlyProfile_HasExpectedFields()
    {
        // v0.6.5.22 (Fix Round 1): nightly Id 不变(pytorch-nightly-cu121),
        //   nightly TorchVersion 是字面量 "nightly",不参与兼容判定,Name 不加后缀。
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
        // v0.6.5.22 (Fix Round 1): CPU profile TorchVersion 跟 stable 一起升 2.4.1。
        var loader = new BaseEnvProfileLoader(_tempDir);
        var p = loader.GetHardcodedDefaults().Single(x => x.CudaVersion == "cpu" && x.Channel == "stable");
        Assert.Equal("2.4.1", p.TorchVersion);
        Assert.Equal("cpu", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
        Assert.Equal(new[] { "torch", "torchaudio", "torchvision" }, p.Packages);
    }

    // ----- LoadAsync -----

    [Fact]
    public async Task LoadAsync_FallsBackWhenFileMissing()
    {
        // File does not exist, no HttpClient → hardcoded defaults (7 profiles).
        // v0.6.5.22 (Fix Round 1): hardcoded 都升 torch 2.4.1,
        //   MarkIncompatibleOlderVersions 不会再标,Id 干净。
        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();
        Assert.Equal(7, profiles.Count);
        Assert.Contains(profiles, p => p.CudaVersion == "cu118" && p.Channel == "stable");
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
        // v0.6.5.22 (Fix Round 1): hardcoded 升 torch 2.4.1,Id 干净。
        Assert.Equal(7, profiles.Count);
        Assert.Contains(profiles, p => p.CudaVersion == "cu118" && p.Channel == "stable");
    }

    [Fact]
    public async Task LoadAsync_EmptyJsonFile_ReturnsDefaults()
    {
        var path = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(path, "");

        var loader = new BaseEnvProfileLoader(_tempDir);
        var profiles = await loader.LoadAsync();

        Assert.Equal(7, profiles.Count);
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
        Assert.Equal(7, profiles.Count);
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
    public async Task GetLiveDefaults_GeneratesSevenProfiles()
    {
        // v0.6.5.18: live 路径默认生成 5 stable (cu118/121/124/126/128) + 1 nightly cu126 + 1 cpu
        var loader = new BaseEnvProfileLoader(_tempDir, FreshCacheDir(), MockedHttpClient(SampleHtml));

        var profiles = await loader.GetLiveDefaultsAsync();

        Assert.Equal(7, profiles.Count);
        var cudas = profiles.Select(p => p.CudaVersion).ToList();
        Assert.Contains("cu118", cudas);
        Assert.Contains("cu121", cudas);
        Assert.Contains("cu124", cudas);
        Assert.Contains("cu126", cudas);
        Assert.Contains("cu128", cudas);
        Assert.Contains("cpu", cudas);
        Assert.Contains(profiles, p => p.Id == "pytorch-nightly-cu126");
        Assert.Contains(profiles, p => p.Id == "pytorch-2.13.0-cu126-stable");
        Assert.Contains(profiles, p => p.Id == "pytorch-2.13.0-cu128-stable");
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
        // 404 → fetcher returns null → hardcoded 7 profiles (nightly cu121)。
        // v0.6.5.22 (Fix Round 1): hardcoded 升 torch 2.4.1,Id 干净,
        //   仍含 pytorch-nightly-cu121 (nightly 字面量 Id 不变)。
        var loader = new BaseEnvProfileLoader(
            _tempDir, FreshCacheDir(), MockedHttpClient("not found", HttpStatusCode.NotFound));

        var profiles = await loader.GetLiveDefaultsAsync();

        Assert.Equal(7, profiles.Count);
        Assert.Contains(profiles, p => p.Id == "pytorch-nightly-cu121");
        Assert.Contains(profiles, p => p.CudaVersion == "cu118" && p.Channel == "stable");
    }

    [Fact]
    public async Task GetLiveDefaults_StaleStable210_PrependsTorch241First()
    {
        // v0.6.5.22 (Fix Round 1):pytorch.org 的 latest_stable 可能 stale
        //   (缓存老 HTML / 网络异常返旧版),如果 BuildLiveDefaults 拿到的
        //   Stable < 2.4,默认 dropdown 第一项会是不兼容版本 → 用户不读文字
        //   直接 Enter → 装 torch 2.1 → comfy_kitchen 启动炸。修法:在
        //   BuildLiveDefaults 顶部 prepend hardcoded torch 2.4.1+cu118,
        //   确保 default 第一项永远是兼容版本。
        var cacheDir = FreshCacheDir();
        WriteCache(cacheDir, new PyTorchLiveVersions
        {
            Stable = "2.1.0",  // stale — pytorch.org 缓存旧版
            HasNightlyCu126 = true,
            FetchedAt = DateTimeOffset.UtcNow,
        });

        var loader = new BaseEnvProfileLoader(
            _tempDir, cacheDir, MockedHttpClient(SampleHtml));

        var profiles = await loader.GetLiveDefaultsAsync();

        // 第一项必须是 comfy_kitchen 兼容版本
        Assert.Equal("2.4.1", profiles[0].TorchVersion);
        Assert.Equal("cu118", profiles[0].CudaVersion);
        // 兼容 Id(无后缀,无 stale 2.1)
        Assert.Equal("pytorch-2.4.1-cu118-stable", profiles[0].Id);
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
        Assert.Equal(7, profiles.Count);
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

        Assert.Equal(7, profiles.Count);
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

    [Theory]
    [InlineData("cu118", "11.8")]
    [InlineData("cu121", "12.1")]
    [InlineData("cu124", "12.4")]
    [InlineData("cu126", "12.6")]
    public async Task LoadProfilesForVersion_MetadataNameContainsCudaLabel(string cudaTag, string expectedLabel)
    {
        var loader = new BaseEnvProfileLoader(_tempDir);
        var metadata = new PyTorchVersion
        {
            Version = "2.5.1",
            CudaVariants = new[] { cudaTag },
            HasCpu = false,
        };

        var profiles = await loader.LoadProfilesForVersionAsync("2.5.1", metadata);

        var p = profiles.Single(x => x.CudaVersion == cudaTag);
        Assert.Contains($"CUDA {expectedLabel}", p.Name);
        Assert.Contains($"CUDA {expectedLabel}", p.Description);
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

    // ----- MarkIncompatibleOlderVersions (v0.6.5.22 T6) -----

    [Fact]
    public void MarkIncompatibleOlderVersions_Torch21_AppendsIncompatibleSuffix()
    {
        // v0.6.5.22: torch < 2.4 跟 comfy_kitchen 的 @torch.library.custom_op
        // 不兼容(decorator 是 PyTorch 2.4 引入),Name 加 (不推荐) 后缀。
        // v0.6.5.22 (Fix Round 1):Id 保持不变(BedProfileId 持久化
        //   到 SQLite 跟 Id 一致,改 Id 会让老 env BED 列显示带后缀)。
        var profile = new BaseEnvProfile
        {
            Id = "torch==2.1.0+cu118",
            Name = "PyTorch 2.1 + CUDA 11.8 (stable)",
            TorchVersion = "2.1.0",
            CudaVersion = "cu118",
            Channel = "stable",
        };

        var result = BaseEnvProfileLoader.MarkIncompatibleOlderVersions(new[] { profile });

        var p = Assert.Single(result);
        // Id 透传 — 不动
        Assert.Equal("torch==2.1.0+cu118", p.Id);
        // Name 加后缀
        Assert.EndsWith(" (不推荐 — comfy_kitchen 不兼容)", p.Name);
        // 关键字段透传:不影响 pip install 命令
        Assert.Equal("2.1.0", p.TorchVersion);
        Assert.Equal("cu118", p.CudaVersion);
        Assert.Equal("stable", p.Channel);
    }

    [Fact]
    public void MarkIncompatibleOlderVersions_Torch24_LeavesIdUnchanged()
    {
        // torch 2.4+ 视为兼容 → 不动 Id / Name
        var profile = new BaseEnvProfile
        {
            Id = "torch==2.4.1+cu118",
            Name = "PyTorch 2.4.1 + CUDA 11.8 (stable)",
            TorchVersion = "2.4.1",
            CudaVersion = "cu118",
            Channel = "stable",
        };

        var result = BaseEnvProfileLoader.MarkIncompatibleOlderVersions(new[] { profile });

        var p = Assert.Single(result);
        Assert.Equal("torch==2.4.1+cu118", p.Id);
        Assert.Equal("PyTorch 2.4.1 + CUDA 11.8 (stable)", p.Name);
        Assert.Equal("2.4.1", p.TorchVersion);
    }

    [Fact]
    public void MarkIncompatibleOlderVersions_Torch15_AppendsIncompatibleSuffix()
    {
        // torch 1.5 是 major<2 边界 → 同样加 Name 后缀(无论 minor 多少)
        // v0.6.5.22 (Fix Round 1):Id 不动,只 Name 加后缀
        var profile = new BaseEnvProfile
        {
            Id = "torch==1.5.0+cpu",
            Name = "PyTorch 1.5.0 (CPU)",
            TorchVersion = "1.5.0",
            CudaVersion = "cpu",
            Channel = "stable",
        };

        var result = BaseEnvProfileLoader.MarkIncompatibleOlderVersions(new[] { profile });

        var p = Assert.Single(result);
        Assert.Equal("torch==1.5.0+cpu", p.Id);
        Assert.EndsWith(" (不推荐 — comfy_kitchen 不兼容)", p.Name);
        Assert.Equal("1.5.0", p.TorchVersion);
    }

    // —— v1.0.0.1 (settings-to-inf):INF 持久化路径 + 老 JSON fallback ——

    [Fact]
    public async Task LoadAsync_InfFileExists_ReturnsFileContents()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var custom = new List<BaseEnvProfile>
        {
            new() { Id = "custom-inf-1", Name = "Custom INF 1", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
            new() { Id = "custom-inf-2", Name = "Custom INF 2", TorchVersion = "2.4.1", CudaVersion = "cu121", Channel = "stable" },
        };
        var json = JsonSerializer.Serialize(custom);
        File.WriteAllText(
            Path.Combine(configDir, "base-env-profiles.inf"),
            $"# base-env-profiles.inf — user override (v1.0.0.1+)\nprofiles = {json}\n");

        var loader = new BaseEnvProfileLoader(localDataDir: _tempDir, configDir: configDir);
        var profiles = await loader.LoadAsync();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("custom-inf-1", profiles[0].Id);
        Assert.Equal("custom-inf-2", profiles[1].Id);
        Assert.Equal("2.4.1", profiles[0].TorchVersion);
    }

    [Fact]
    public async Task LoadAsync_InfPrefersOverJson_WhenBothExist()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        // .inf 写 "from-inf"
        var infProfiles = new List<BaseEnvProfile>
        {
            new() { Id = "from-inf", Name = "INF", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
        };
        var infJson = JsonSerializer.Serialize(infProfiles);
        File.WriteAllText(
            Path.Combine(configDir, "base-env-profiles.inf"),
            $"profiles = {infJson}\n");

        // .json 写 "from-json"
        var jsonProfiles = new List<BaseEnvProfile>
        {
            new() { Id = "from-json", Name = "JSON", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
        };
        var jsonPath = Path.Combine(_tempDir, "base_env_profiles.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(jsonProfiles));

        var loader = new BaseEnvProfileLoader(localDataDir: _tempDir, configDir: configDir);
        var profiles = await loader.LoadAsync();

        Assert.Single(profiles);
        Assert.Equal("from-inf", profiles[0].Id);
    }

    [Fact]
    public async Task LoadAsync_LegacyJsonOnly_StillWorks()
    {
        // 没有 .inf,只有老 .json — fallback 兼容
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        var custom = new List<BaseEnvProfile>
        {
            new() { Id = "legacy-1", Name = "Legacy 1", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "base_env_profiles.json"),
            JsonSerializer.Serialize(custom));

        var loader = new BaseEnvProfileLoader(localDataDir: _tempDir, configDir: configDir);
        var profiles = await loader.LoadAsync();

        Assert.Single(profiles);
        Assert.Equal("legacy-1", profiles[0].Id);
    }

    [Fact]
    public async Task LoadAsync_CorruptInf_FallsBackToLegacyJson()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        // .inf 故意写坏
        File.WriteAllText(
            Path.Combine(configDir, "base-env-profiles.inf"),
            "profiles = not valid json [[[");

        // .json 仍然有效
        var custom = new List<BaseEnvProfile>
        {
            new() { Id = "fallback-1", Name = "F", TorchVersion = "2.4.1", CudaVersion = "cu118", Channel = "stable" },
        };
        File.WriteAllText(
            Path.Combine(_tempDir, "base_env_profiles.json"),
            JsonSerializer.Serialize(custom));

        var loader = new BaseEnvProfileLoader(localDataDir: _tempDir, configDir: configDir);
        var profiles = await loader.LoadAsync();

        Assert.Single(profiles);
        Assert.Equal("fallback-1", profiles[0].Id);
    }
}
