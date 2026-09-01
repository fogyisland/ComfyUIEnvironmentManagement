using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T28:锁 <see cref="FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync"/>
/// 对 Fooocus <c>fooocus_expansion</c> HF repo 元数据文件补下 ——
/// T22 只下 <c>pytorch_model.bin</c>,但 <c>extras/expansion.py</c> 需要 6 个
/// 元数据文件(config.json / tokenizer_config.json / special_tokens_map.json /
/// vocab.json / merges.txt / positive.txt),否则 pipeline init 抛 OSError。
///
/// 镜像 <see cref="FooocusAllModelsCheckerTests"/> + <see cref="FooocusDefaultModelsInstallerTests"/>
/// 的 HttpMessageHandler stub 模式(测试不真起网络,handler stub 返回固定字节)。
/// </summary>
public sealed class FooocusExpansionMetadataTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _envsDir;
    private readonly StubHttpHandler _handler;

    public FooocusExpansionMetadataTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(),
            "fooocus-expansion-meta-" + Guid.NewGuid().ToString("N")[..8]);
        _envsDir = Path.Combine(_projectRoot, "envs");
        Directory.CreateDirectory(_envsDir);
        _handler = new StubHttpHandler();
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
        _handler.Dispose();
    }

    private Environment MakeFooocusEnv(string name = "FocusAll")
    {
        var envDir = Path.Combine(_envsDir, name);
        var expansionDir = Path.Combine(envDir, "models", "prompt_expansion", "fooocus_expansion");
        Directory.CreateDirectory(expansionDir);
        // 模拟 T22 已下 pytorch_model.bin(351MB 大文件)— installer 应该跳过它
        File.WriteAllText(Path.Combine(expansionDir, "pytorch_model.bin"),
            new string('x', 1024));  // 1KB stub,真实场景是 351MB

        return new Environment
        {
            Id = "fooocus-expansion-test",
            Name = name,
            Status = "stopped",
            TemplateKind = "Fooocus",
            RootPath = envDir,
        };
    }

    [Fact]
    public async Task EnsureExpansionMetadata_AllFilesAlreadyExist_ReturnsTrueWithoutHttpCall()
    {
        // T28:idempotent — 6 个元数据都已下 → 跳过 download,直接返 true
        var env = MakeFooocusEnv();
        var expansionDir = Path.Combine(env.RootPath, "models", "prompt_expansion", "fooocus_expansion");
        foreach (var name in FooocusExpansionMetadataConstants.MetadataFileNames)
        {
            File.WriteAllText(Path.Combine(expansionDir, name), $"stub {name}");
        }
        _handler.SimulateFailure = true;  // 如果调了 HttpClient 就抛

        var result = await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings: null);

        Assert.True(result);
        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task EnsureExpansionMetadata_DownloadsOnlyMissingFiles()
    {
        // T28:已有 3 个元数据 → 只下剩下 3 个(pytorch_model.bin 不在 list 里)
        var env = MakeFooocusEnv();
        var expansionDir = Path.Combine(env.RootPath, "models", "prompt_expansion", "fooocus_expansion");
        var alreadyHave = new[] { "config.json", "tokenizer_config.json", "vocab.json" };
        foreach (var name in alreadyHave)
        {
            File.WriteAllText(Path.Combine(expansionDir, name), $"pre-existing {name}");
        }
        var requestedUrls = new System.Collections.Generic.List<string>();
        _handler.OnRequest = url => requestedUrls.Add(url);

        var result = await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings: null, http: new HttpClient(_handler));

        Assert.True(result);
        // 6 - 3 = 3 个 missing,各调 1 次 HTTP
        Assert.Equal(3, requestedUrls.Count);
        // 不应该请求已存在的 3 个
        Assert.DoesNotContain(requestedUrls, u => u.Contains("config.json"));
        Assert.DoesNotContain(requestedUrls, u => u.Contains("tokenizer_config.json"));
        Assert.DoesNotContain(requestedUrls, u => u.Contains("vocab.json"));
        // 应该有剩下 3 个
        Assert.Contains(requestedUrls, u => u.Contains("special_tokens_map.json"));
        Assert.Contains(requestedUrls, u => u.Contains("merges.txt"));
        Assert.Contains(requestedUrls, u => u.Contains("positive.txt"));
    }

    [Fact]
    public async Task EnsureExpansionMetadata_WritesAllSixFiles()
    {
        // T28:全新 env(无元数据)→ 6 个全部下完
        var env = MakeFooocusEnv();
        var expansionDir = Path.Combine(env.RootPath, "models", "prompt_expansion", "fooocus_expansion");
        // 删 pytorch_model.bin 让 env 更"空"
        File.Delete(Path.Combine(expansionDir, "pytorch_model.bin"));

        var result = await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings: null, http: new HttpClient(_handler));

        Assert.True(result);
        foreach (var name in FooocusExpansionMetadataConstants.MetadataFileNames)
        {
            Assert.True(File.Exists(Path.Combine(expansionDir, name)),
                $"missing file: {name}");
        }
        // pytorch_model.bin 不应该被重下或新建
        Assert.False(File.Exists(Path.Combine(expansionDir, "pytorch_model.bin")));
    }

    [Fact]
    public async Task EnsureExpansionMetadata_AppliesHfMirror_WhenSettingsConfigured()
    {
        // T28:跟 T22/T23b 一样,Settings.ModelSourceHuggingFaceUseMirror +
        // ModelSourceHuggingFaceMirrorUrl 改 URL host
        var env = MakeFooocusEnv();
        var settings = new Settings
        {
            ModelSourceHuggingFaceUseMirror = true,
            ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com",
        };
        string? capturedUrl = null;
        _handler.OnRequest = url => capturedUrl = url;

        await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings, http: new HttpClient(_handler));

        Assert.NotNull(capturedUrl);
        Assert.StartsWith("https://hf-mirror.com/lllyasviel/fooocus_expansion/resolve/main/", capturedUrl);
        Assert.DoesNotContain("huggingface.co", capturedUrl);
    }

    [Fact]
    public async Task EnsureExpansionMetadata_HttpFailure_ReturnsFalse_LogsError()
    {
        // T28:best-effort — 网络 fail 应该返 false + logProgress warn,不抛
        var env = MakeFooocusEnv();
        var progressMessages = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(m => progressMessages.Add(m));
        _handler.SimulateFailure = true;

        var result = await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings: null, http: new HttpClient(_handler), logProgress: progress);

        Assert.False(result);
        Assert.Contains(progressMessages, m => m.Contains("✗"));
    }

    [Fact]
    public async Task EnsureExpansionMetadata_EmptyRootPath_ReturnsFalse()
    {
        // 防御性:env.RootPath 空 → 不抛,返 false
        var env = MakeFooocusEnv();
        env.RootPath = "";

        var result = await FooocusDefaultModelsInstaller.EnsureExpansionMetadataAsync(
            env, settings: null);

        Assert.False(result);
    }

    [Fact]
    public void MetadataFileNames_DoesNotContainPytorchModelBin()
    {
        // T28:常量 list 不含 pytorch_model.bin(T22 已下,不要重下 351MB)
        Assert.DoesNotContain("pytorch_model.bin", FooocusExpansionMetadataConstants.MetadataFileNames);
    }

    [Fact]
    public void MetadataFileNames_HasExactlySixEntries()
    {
        // T28:锁 6 个条目(config + 4 tokenizer + positive.txt)防 drift
        Assert.Equal(6, FooocusExpansionMetadataConstants.MetadataFileNames.Count);
    }

    // ── Stub HTTP handler:不真起网络,返固定响应 ──

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool SimulateFailure { get; set; }
        public Action<string>? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            OnRequest?.Invoke(request.RequestUri?.ToString() ?? "");
            if (SimulateFailure)
            {
                throw new HttpRequestException("simulated network failure");
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"stub\": true}", System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}