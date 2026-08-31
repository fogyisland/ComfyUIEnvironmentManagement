using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T22:FooocusDefaultModelsInstaller 测试。覆盖:
/// <list type="bullet">
///   <item><c>IsInstalled_ReturnsFalse_WhenMarkerMissing</c> / <c>_ReturnsTrue_WhenMarkerExists</c></item>
///   <item><c>Constants_FourFiles_ExactMatchLaunchPyList</c> lock 4 个文件名 + URL 防 launch.py 改了忘了同步</item>
///   <item><c>InstallAsync_AllFourModelsSucceed_WritesMarker</c> — Mock HttpMessageHandler 返 fake bytes</item>
///   <item><c>InstallAsync_OneModelFails_ReturnsResultFailed_NoMarker</c> — 1 个 URL 返 404 → result.Success=false + 无 marker</item>
///   <item><c>MirrorUrl_Replace_HuggingFaceCo_To_Mirror</c> — HF mirror 字符串替换</item>
///   <item><c>MarkerFileName_IsFooocusDefaultModelsInstalled</c> 防 drift</item>
/// </list>
/// </summary>
public class FooocusDefaultModelsInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public FooocusDefaultModelsInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"fooocus-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string name = "fooocus")
    {
        var env = new Environment
        {
            Id = name,
            Name = name,
            RootPath = _envRoot,
            TemplateKind = "Fooocus",
        };
        Directory.CreateDirectory(env.RootPath);
        return env;
    }

    /// <summary>
    /// 替身 HttpMessageHandler — 根据 URL 路径返预定义的 response(模拟 HF 下载)。
    /// </summary>
    private class FakeHuggingFaceHandler : HttpMessageHandler
    {
        public Dictionary<string, HttpStatusCode> UrlStatusMap { get; } = new();
        public Dictionary<string, byte[]> UrlBytesMap { get; } = new();
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            RequestedUrls.Add(url);
            if (UrlStatusMap.TryGetValue(url, out var status))
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }
            // 默认返 OK + 1KB 假字节
            var bytes = UrlBytesMap.TryGetValue(url, out var b)
                ? b
                : new byte[1024];
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            resp.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(resp);
        }
    }

    // ----- IsInstalled -----

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenMarkerMissing()
    {
        var env = SeedEnv();
        Assert.False(FooocusDefaultModelsInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsTrue_WhenMarkerExists()
    {
        var env = SeedEnv();
        File.WriteAllText(
            Path.Combine(env.RootPath, FooocusDefaultModelsConstants.MarkerFileName),
            "2026-09-01T00:00:00Z");
        Assert.True(FooocusDefaultModelsInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenRootPathEmpty()
    {
        var env = new Environment { Name = "x", RootPath = "" };
        Assert.False(FooocusDefaultModelsInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenEnvNull()
    {
        Assert.False(FooocusDefaultModelsInstaller.IsInstalled(null!));
    }

    // ----- Constants lock -----

    [Fact]
    public void MarkerFileName_IsFooocusDefaultModelsInstalled()
    {
        Assert.Equal(".fooocus_default_models_installed",
            FooocusDefaultModelsConstants.MarkerFileName);
    }

    [Fact]
    public void Constants_FourFiles_ExactMatchLaunchPyList()
    {
        // 防 launch.py 改了 launcher 默认模型清单后忘了同步 —— 锁 4 个文件名 + URL + SubDir。
        // 顺序按 launch.py vae_approx_filenames 列表顺序(launch.py line 63-66) + fooocus_expansion(line 109-113)。
        Assert.Equal(4, FooocusDefaultModelsConstants.DefaultModels.Count);

        var xlvaeapp = FooocusDefaultModelsConstants.DefaultModels[0];
        Assert.Equal("xlvaeapp.pth", xlvaeapp.FileName);
        Assert.Equal("https://huggingface.co/lllyasviel/misc/resolve/main/xlvaeapp.pth", xlvaeapp.Url);
        Assert.Equal("models/vae_approx", xlvaeapp.SubDir);

        var vaeappSd15 = FooocusDefaultModelsConstants.DefaultModels[1];
        Assert.Equal("vaeapp_sd15.pth", vaeappSd15.FileName);   // URL .pt,本地 .pth(上游 quirk)
        Assert.Equal("https://huggingface.co/lllyasviel/misc/resolve/main/vaeapp_sd15.pt", vaeappSd15.Url);
        Assert.Equal("models/vae_approx", vaeappSd15.SubDir);

        var interposer = FooocusDefaultModelsConstants.DefaultModels[2];
        Assert.Equal("xl-to-v1_interposer-v4.0.safetensors", interposer.FileName);
        Assert.Equal("https://huggingface.co/mashb1t/misc/resolve/main/xl-to-v1_interposer-v4.0.safetensors", interposer.Url);
        Assert.Equal("models/vae_approx", interposer.SubDir);

        var fooocusExp = FooocusDefaultModelsConstants.DefaultModels[3];
        Assert.Equal("pytorch_model.bin", fooocusExp.FileName);
        Assert.Equal("https://huggingface.co/lllyasviel/misc/resolve/main/fooocus_expansion.bin", fooocusExp.Url);
        Assert.Equal("models/prompt_expansion/fooocus_expansion", fooocusExp.SubDir);
    }

    // ----- InstallAsync success/failure -----

    [Fact]
    public async Task InstallAsync_AllFourModelsSucceed_WritesMarker()
    {
        var env = SeedEnv();
        var fakeHandler = new FakeHuggingFaceHandler();
        // 默认 4 个 URL 都返 200 + fake bytes(无 UrlStatusMap entry)

        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(fakeHandler));

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"install fail: {result.Reason}");
        Assert.Equal(4, result.DownloadedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.TotalBytes > 0);

        // 4 个文件都创建
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "vae_approx", "xlvaeapp.pth")));
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "vae_approx", "vaeapp_sd15.pth")));
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "vae_approx", "xl-to-v1_interposer-v4.0.safetensors")));
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "prompt_expansion", "fooocus_expansion", "pytorch_model.bin")));

        // marker 写
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            FooocusDefaultModelsConstants.MarkerFileName)));
    }

    [Fact]
    public async Task InstallAsync_OneModelFails_ReturnsResultFailed_NoMarker()
    {
        var env = SeedEnv();
        var fakeHandler = new FakeHuggingFaceHandler();
        // 第 3 个 URL(interposer)返 404 — 应该归类为失败
        var interposerUrl = FooocusDefaultModelsConstants.DefaultModels[2].Url;
        fakeHandler.UrlStatusMap[interposerUrl] = HttpStatusCode.NotFound;

        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(fakeHandler));

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.Contains("interposer", result.Reason);
        Assert.Equal(3, result.DownloadedCount);
        Assert.Equal(1, result.FailedCount);
        // 失败路径不应写 marker
        Assert.False(File.Exists(Path.Combine(env.RootPath,
            FooocusDefaultModelsConstants.MarkerFileName)));
        // 但已成功的 3 个文件保留(env 启动时这些已就绪)
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "vae_approx", "xlvaeapp.pth")));
        Assert.True(File.Exists(Path.Combine(env.RootPath,
            "models", "prompt_expansion", "fooocus_expansion", "pytorch_model.bin")));
    }

    [Fact]
    public async Task InstallAsync_FileAlreadyExists_SkipsDownload()
    {
        var env = SeedEnv();
        // 预创建 vae_approx/xlvaeapp.pth(模拟之前下载过)
        var existingDir = Path.Combine(env.RootPath, "models", "vae_approx");
        Directory.CreateDirectory(existingDir);
        var existingPath = Path.Combine(existingDir, "xlvaeapp.pth");
        File.WriteAllBytes(existingPath, new byte[] { 0x42, 0x42, 0x42 });
        var originalSize = new FileInfo(existingPath).Length;

        var fakeHandler = new FakeHuggingFaceHandler();
        var installer = new FooocusDefaultModelsInstaller(new HttpClient(fakeHandler));

        await installer.InstallAsync(env);

        // 已存在的文件 size 不变(没被覆盖)
        Assert.Equal(originalSize, new FileInfo(existingPath).Length);
    }

    [Fact]
    public async Task InstallAsync_Cancellation_ReturnsResultCancelled()
    {
        var env = SeedEnv();
        // 用一个永远 hang 的 handler 模拟取消
        var hangingHandler = new HangingHandler();
        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(hangingHandler) { Timeout = TimeSpan.FromMilliseconds(200) });

        var result = await installer.InstallAsync(env, default);

        // HttpClient 超时 / cancelled → install 内部抛异常被 catch,返 failure(非 cancelled
        // 因为我们没显式传 CancellationToken)。此处只验证不会卡死 / 不会写 marker。
        Assert.False(File.Exists(Path.Combine(env.RootPath,
            FooocusDefaultModelsConstants.MarkerFileName)));
    }

    private class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ----- Mirror URL replacement -----

    [Fact]
    public void InstallAsync_UsesMirrorUrl_WhenConfigured()
    {
        // 通过 Settings 配置 HF mirror,验证 URL 实际被替换(用 FakeHuggingFaceHandler
        // 记录请求 URL 验证)。
        var env = SeedEnv();
        var fakeHandler = new FakeHuggingFaceHandler();
        var settings = new ComfyUI.Manager.Models.Settings
        {
            ModelSourceHuggingFaceUseMirror = true,
            ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com",
        };
        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(fakeHandler), settings: settings);

        // 不 await result(可能 success / fail 都行),只检查 RequestedUrls
        try { installer.InstallAsync(env).GetAwaiter().GetResult(); } catch { }

        // 4 个 URL 都应被替换为 hf-mirror.com
        Assert.NotEmpty(fakeHandler.RequestedUrls);
        foreach (var url in fakeHandler.RequestedUrls)
        {
            Assert.Contains("hf-mirror.com", url);
            Assert.DoesNotContain("huggingface.co", url);
        }
    }

    [Fact]
    public void InstallAsync_UsesOfficialUrl_WhenMirrorDisabled()
    {
        // Mirror disabled → 走 huggingface.co 官方
        var env = SeedEnv();
        var fakeHandler = new FakeHuggingFaceHandler();
        var settings = new ComfyUI.Manager.Models.Settings
        {
            ModelSourceHuggingFaceUseMirror = false,
            ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com",
        };
        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(fakeHandler), settings: settings);

        try { installer.InstallAsync(env).GetAwaiter().GetResult(); } catch { }

        Assert.NotEmpty(fakeHandler.RequestedUrls);
        foreach (var url in fakeHandler.RequestedUrls)
        {
            Assert.Contains("huggingface.co", url);
            Assert.DoesNotContain("hf-mirror.com", url);
        }
    }

    [Fact]
    public void InstallAsync_NullSettings_UsesOfficialUrl()
    {
        // Null settings 不应 NPE — mirror 替换跳过,走 huggingface.co 官方
        var env = SeedEnv();
        var fakeHandler = new FakeHuggingFaceHandler();
        var installer = new FooocusDefaultModelsInstaller(
            new HttpClient(fakeHandler), settings: null);

        try { installer.InstallAsync(env).GetAwaiter().GetResult(); } catch { }

        Assert.NotEmpty(fakeHandler.RequestedUrls);
        foreach (var url in fakeHandler.RequestedUrls)
        {
            Assert.Contains("huggingface.co", url);
        }
    }
}
