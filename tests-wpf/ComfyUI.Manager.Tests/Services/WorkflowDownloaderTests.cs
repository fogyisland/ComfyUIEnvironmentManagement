using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class WorkflowDownloaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkflowDownloader _dl;

    public WorkflowDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrWFDl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var handler = new MultiResponseHandler();
        _dl = new WorkflowDownloader(new HttpClient(handler), logger: null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WorkflowEntry Entry(string id = "abc12345", string title = "Portrait Gen",
        string jsonUrl = "https://x/wf.json", string? previewUrl = null)
        => new()
        {
            Source = WorkflowSourceKind.CommunityJson,
            SourceId = id,
            SourceUrl = "https://x/page",
            WorkflowJsonUrl = jsonUrl,
            PreviewImageUrl = previewUrl,
            Title = title,
        };

    [Fact]
    public async Task DownloadAsync_WritesWorkflowAndMeta()
    {
        var wfJson = "{\"nodes\":[]}";
        var entry = Entry(jsonUrl: "https://x/wf1.json");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        Assert.NotNull(result.SubfolderPath);
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "workflow.json")));
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "meta.json")));
        var metaContent = File.ReadAllText(Path.Combine(result.SubfolderPath!, "meta.json"));
        Assert.Contains("Portrait Gen", metaContent);
        Assert.Contains("abc12345", metaContent);
    }

    [Fact]
    public async Task DownloadAsync_PreviewUrl_WritesPreviewFile()
    {
        var entry = Entry(jsonUrl: "https://x/wf.json", previewUrl: "https://x/preview.png");
        // multi-response handler returns preview bytes for /preview.png

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        var previewFile = Directory.GetFiles(result.SubfolderPath!, "*.preview.*").FirstOrDefault();
        Assert.NotNull(previewFile);
        Assert.EndsWith(".png", previewFile);
    }

    [Fact]
    public async Task DownloadAsync_Preview404_StillWritesWorkflowAndMeta()
    {
        var entry = Entry(jsonUrl: "https://x/wf.json", previewUrl: "https://x/missing.png");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(result.SubfolderPath!, "workflow.json")));
        Assert.Empty(Directory.GetFiles(result.SubfolderPath!, "*.preview.*"));
    }

    [Fact]
    public async Task DownloadAsync_JsonUrl404_ReturnsFail()
    {
        var entry = Entry(jsonUrl: "https://x/missing-wf.json");

        var result = await _dl.DownloadAsync(entry, _tempDir);

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task DownloadAsync_EmptyDir_ReturnsFail()
    {
        var entry = Entry();

        var result = await _dl.DownloadAsync(entry, workflowsDir: "");

        Assert.False(result.Success);
        Assert.Contains("empty", result.FailureReason);
    }

    [Fact]
    public async Task DownloadAsync_SubfolderCollision_AppendsSuffix()
    {
        var entry1 = Entry(id: "aaaaaaaa", title: "Same Title");
        var entry2 = Entry(id: "aaaaaaaa", title: "Same Title");  // same sourceId+title → same slug

        var r1 = await _dl.DownloadAsync(entry1, _tempDir);
        var r2 = await _dl.DownloadAsync(entry2, _tempDir);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotEqual(r1.SubfolderPath, r2.SubfolderPath);
        Assert.True(r2.SubfolderPath!.Contains("-1") || r2.SubfolderPath.EndsWith("-1"));
    }

    [Fact]
    public async Task DownloadBatchAsync_RunsInParallel_BothSucceed()
    {
        var entries = new[]
        {
            Entry(id: "11111111", title: "A", jsonUrl: "https://x/a.json"),
            Entry(id: "22222222", title: "B", jsonUrl: "https://x/b.json"),
            Entry(id: "33333333", title: "C", jsonUrl: "https://x/c.json"),
        };

        var summary = await _dl.DownloadBatchAsync(entries, _tempDir);

        Assert.Equal(3, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(3, Directory.GetDirectories(_tempDir).Length);
    }

    /// <summary>路由多个 URL 到不同响应 — wf.json / *.preview.png → OK;missing.* → 404。</summary>
    private sealed class MultiResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            HttpResponseMessage resp;
            if (url.Contains("missing"))
            {
                resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            else if (url.EndsWith(".png") || url.EndsWith(".jpg"))
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF }),  // fake JPEG
                };
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"nodes\":[]}", Encoding.UTF8, "application/json"),
                };
            }
            return Task.FromResult(resp);
        }
    }
}
