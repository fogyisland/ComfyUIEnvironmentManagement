using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public sealed class JunctionLinkerGetTargetTests : IDisposable
{
    private readonly string _root;

    public JunctionLinkerGetTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "juncgettarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetTargetAsync_ExistingJunction_ReturnsTarget()
    {
        var linker = new JunctionLinker();
        var realDir = Path.Combine(_root, "real-target");
        Directory.CreateDirectory(realDir);
        var link = Path.Combine(_root, "link-to-real");
        await linker.CreateAsync(link, realDir, CancellationToken.None);

        var target = await linker.GetTargetAsync(link, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(
            Path.GetFullPath(realDir),
            Path.GetFullPath(target!),
            ignoreCase: true);
    }

    [Fact]
    public async Task GetTargetAsync_RegularDirectory_ReturnsNull()
    {
        var linker = new JunctionLinker();
        var regular = Path.Combine(_root, "regular");
        Directory.CreateDirectory(regular);

        var target = await linker.GetTargetAsync(regular, CancellationToken.None);

        Assert.Null(target);
    }

    [Fact]
    public async Task GetTargetAsync_NonExistentPath_Throws()
    {
        var linker = new JunctionLinker();
        var ghost = Path.Combine(_root, "ghost");

        await Assert.ThrowsAsync<JunctionLinker.JunctionCreationException>(() =>
            linker.GetTargetAsync(ghost, CancellationToken.None));
    }
}
