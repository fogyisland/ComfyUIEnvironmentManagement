using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelDownloaderTests : IDisposable
{
    private readonly string _tmp;
    private readonly ModelDelegatingHandlerStub _handler;

    public ModelDownloaderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ComfyUIMgrDl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _handler = new ModelDelegatingHandlerStub();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private ModelDownloader NewDownloader() => new ModelDownloader(new HttpClient(_handler), null);

    private ModelVersionEntry MakeVersion(string title = "Realistic Vision", string modelId = "12345", string versionId = "67890")
    {
        var entry = new ModelEntry
        {
            Source = ModelSourceKind.CivitAi,
            SourceId = modelId,
            Title = title,
            Kind = ModelKind.Checkpoint,
            BaseModel = "SD 1.5",
        };
        return new ModelVersionEntry
        {
            Id = $"CivitAi:{modelId}:{versionId}",
            Parent = entry,
            SourceVersionId = versionId,
            Name = "v5.0 fp16",
            BaseModel = "SD 1.5",
            SizeBytes = 1024,
            PrimaryDownloadUrl = "https://cdn.example.com/model.safetensors",
            Files = new List<ModelFile> {
                new ModelFile {
                    Name = "model.safetensors",
                    Format = "Safe Tensor",
                    SizeBytes = 1024,
                    DownloadUrl = "https://cdn.example.com/model.safetensors",
                    IsPrimary = true
                }
            },
        };
    }

    [Fact]
    public async Task DownloadAsync_WritesFileAndMeta_ReturnsSuccess()
    {
        var fakeBytes = new byte[1024];
        for (var i = 0; i < fakeBytes.Length; i++) fakeBytes[i] = (byte)(i % 256);
        _handler.Enqueue(HttpStatusCode.OK, fakeBytes, contentLength: 1024);

        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, log: null, progress: null, default);

        Assert.True(result.Success);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath!));
        Assert.Equal(1024, result.SizeBytes);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(result.FilePath)!, "meta.json")));
    }

    [Fact]
    public async Task DownloadAsync_ProgressCallback_FiresMonotonic()
    {
        var fakeBytes = new byte[4096];
        _handler.Enqueue(HttpStatusCode.OK, fakeBytes, contentLength: 4096);

        var reports = new List<ModelDownloadProgress>();
        var progress = new Progress<ModelDownloadProgress>(p => reports.Add(p));

        var v = MakeVersion();
        await NewDownloader().DownloadAsync(v, _tmp, log: null, progress: progress, default);

        // Allow async Progress<T> callbacks to flush
        await Task.Delay(100);

        Assert.NotEmpty(reports);
        for (var i = 1; i < reports.Count; i++)
            Assert.True(reports[i].BytesDownloaded >= reports[i - 1].BytesDownloaded);
        Assert.Equal(4096, reports[^1].BytesDownloaded);
        Assert.Equal(4096, reports[^1].TotalBytes);
    }

    [Fact]
    public async Task DownloadAsync_HttpError_ReturnsFail()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "Not Found");

        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
        Assert.Null(result.FilePath);
    }

    [Fact]
    public async Task DownloadAsync_VersionFolderExists_CollisionSuffixAdded()
    {
        // "v5.0 fp16" → slug "v5-0-fp16" (period becomes '-', space becomes '-'), id8 from "67890" → "67890000"
        // Model: "Realistic Vision" + sourceId "12345" → slug "realistic-vision-12345000" (id8 padded)
        var kindDir = Path.Combine(_tmp, "checkpoints");
        var modelDir = Path.Combine(kindDir, "realistic-vision-12345000");
        var existingDir = Path.Combine(modelDir, "v5-0-fp16-67890000");
        Directory.CreateDirectory(existingDir);

        _handler.Enqueue(HttpStatusCode.OK, new byte[10], contentLength: 10);
        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        Assert.True(result.Success);
        Assert.Contains("v5-0-fp16-67890000-1", result.FilePath!);
    }

    [Fact]
    public async Task DownloadAsync_PartialFileCleanedOnFailure()
    {
        _handler.Enqueue(HttpStatusCode.InternalServerError, "");

        var v = MakeVersion();
        await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        // No .partial should remain
        var partials = Directory.GetFiles(_tmp, "*.partial", SearchOption.AllDirectories);
        Assert.Empty(partials);
    }

    [Fact]
    public async Task DownloadBatchAsync_Parallel4_AllSucceed()
    {
        for (var i = 0; i < 5; i++)
            _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);

        var versions = new List<ModelVersionEntry>();
        for (var i = 0; i < 5; i++)
            versions.Add(MakeVersion(versionId: $"v{i}"));

        var summary = await NewDownloader().DownloadBatchAsync(versions, _tmp, log: null, default);

        Assert.Equal(5, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(500, summary.TotalBytesDownloaded);
    }

    [Fact]
    public async Task DownloadBatchAsync_OneFails_OthersSucceed()
    {
        _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);
        _handler.Enqueue(HttpStatusCode.NotFound, "fail");
        _handler.Enqueue(HttpStatusCode.OK, new byte[100], contentLength: 100);

        var versions = new List<ModelVersionEntry> {
            MakeVersion(versionId: "v0"),
            MakeVersion(versionId: "v1"),
            MakeVersion(versionId: "v2"),
        };

        var summary = await NewDownloader().DownloadBatchAsync(versions, _tmp, null, default);

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Single(summary.Errors);
    }

    [Fact]
    public async Task DownloadAsync_MetaJsonContainsRequiredFields()
    {
        _handler.Enqueue(HttpStatusCode.OK, new byte[10], contentLength: 10);
        var v = MakeVersion();
        var result = await NewDownloader().DownloadAsync(v, _tmp, null, null, default);

        var metaPath = Path.Combine(Path.GetDirectoryName(result.FilePath)!, "meta.json");
        var json = await File.ReadAllTextAsync(metaPath);
        Assert.Contains("\"title\"", json);
        Assert.Contains("Realistic Vision", json);
        Assert.Contains("\"kind\"", json);
        Assert.Contains("Checkpoint", json);
        Assert.Contains("\"source_id\"", json);
        Assert.Contains("12345", json);
        Assert.Contains("\"source_version_id\"", json);
        Assert.Contains("67890", json);
        Assert.Contains("\"downloaded_at\"", json);
    }
}

internal class ModelDelegatingHandlerStub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode, byte[], long?)> _responses = new();

    public void Enqueue(HttpStatusCode code, byte[] body, long? contentLength = null)
    {
        _responses.Enqueue((code, body, contentLength));
    }

    public void Enqueue(HttpStatusCode code, string body)
    {
        var bytes = string.IsNullOrEmpty(body) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(body);
        _responses.Enqueue((code, bytes, (long?)null));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var (code, body, len) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, Array.Empty<byte>(), (long?)null);
        var msg = new HttpResponseMessage(code)
        {
            Content = new ByteArrayContent(body),
        };
        if (len.HasValue)
        {
            msg.Content.Headers.ContentLength = len.Value;
        }
        return Task.FromResult(msg);
    }
}
