using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class Ltx2WrapperGeneratorTests : IDisposable
{
    private readonly string _envRoot;

    public Ltx2WrapperGeneratorTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            "ltx2wrapper-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task GenerateAsync_CreatesTwoWrapperBats()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-distilled.bat")));
        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-dfr.bat")));
    }

    [Fact]
    public async Task GenerateAsync_Distilled_BatContent_UsesDp0AndLtxPipelinesDistilled()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_envRoot, "run-ltx2-distilled.bat"));
        Assert.Contains("%~dp0tools\\uv\\uv.exe", content);
        Assert.Contains("uv.exe\" run python -m ltx_pipelines.distilled", content);
        Assert.Contains("%*", content);   // 透传参数
    }

    [Fact]
    public async Task GenerateAsync_Dfr_BatContent_UsesDfrPipeline()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(Path.Combine(_envRoot, "run-ltx2-dfr.bat"));
        Assert.Contains("%~dp0tools\\uv\\uv.exe", content);
        Assert.Contains("uv.exe\" run python -m ltx_pipelines.dfr_pipeline", content);
    }

    [Fact]
    public async Task GenerateAsync_Idempotent_OverwritesCleanly()
    {
        var gen = new Ltx2WrapperGenerator(_envRoot);
        await gen.GenerateAsync(CancellationToken.None);
        // 第一次写完留个垃圾文件
        var strayBat = Path.Combine(_envRoot, "run-ltx2-distilled.bat.bak");
        await File.WriteAllTextAsync(strayBat, "leftover");
        // 再跑一次不应删 stray
        await gen.GenerateAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_envRoot, "run-ltx2-distilled.bat")));
        Assert.True(File.Exists(strayBat));   // 不删无关文件
    }
}
