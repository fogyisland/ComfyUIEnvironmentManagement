using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class UvInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public UvInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            "uvinstaller-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private static byte[] FakeUvZip()
    {
        // zip 内有 uv.exe 字节 "fake-uv-binary"
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("uv.exe");
            using var es = entry.Open();
            var bytes = System.Text.Encoding.UTF8.GetBytes("fake-uv-binary");
            es.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static (byte[] stdout, int exitCode) FakeUvVersionOk()
        => (System.Text.Encoding.UTF8.GetBytes("uv 0.5.0\n"), 0);

    private UvInstaller MakeInstaller(byte[] zipBytes, (byte[] stdout, int exitCode)? versionProbe = null)
    {
        var versionProbe1 = versionProbe ?? FakeUvVersionOk();
        return new UvInstaller(
            envRoot: _envRoot,
            downloader: (_, _, _) => Task.FromResult(zipBytes),
            versionProber: (_, _) =>
            {
                var (stdout, exit) = versionProbe1;
                return Task.FromResult((stdout, exit));
            });
    }

    [Fact]
    public async Task InstallAsync_DownloadsAndExtracts_ReturnsExePath()
    {
        var installer = MakeInstaller(FakeUvZip());
        var exePath = await installer.InstallAsync(CancellationToken.None);

        Assert.True(File.Exists(exePath));
        Assert.EndsWith("uv.exe", exePath);
        Assert.Equal("fake-uv-binary", await File.ReadAllTextAsync(exePath));
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_SkipsDownload()
    {
        var installer = MakeInstaller(FakeUvZip());
        // 第一次安装
        var firstPath = await installer.InstallAsync(CancellationToken.None);
        // 第二次:downloader 改成抛异常的,验证没被调用
        var secondInstaller = new UvInstaller(
            envRoot: _envRoot,
            downloader: (_, _, _) => throw new InvalidOperationException("should not download"),
            versionProber: (_, _) => Task.FromResult(FakeUvVersionOk()));
        var secondPath = await secondInstaller.InstallAsync(CancellationToken.None);

        Assert.Equal(firstPath, secondPath);
    }

    [Fact]
    public async Task InstallAsync_VersionProbeFails_Throws()
    {
        var installer = MakeInstaller(FakeUvZip(),
            versionProbe: (Array.Empty<byte>(), 1));  // exit 1 = 失败
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_EmptyDownload_Throws()
    {
        var installer = MakeInstaller(Array.Empty<byte>());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InstallAsync_ZeroLengthFile_NotCountedAsInstalled()
    {
        // 模拟 uv.exe 存在但 0 字节(损坏)→ 必须重下
        var uvDir = Path.Combine(_envRoot, "tools", "uv");
        Directory.CreateDirectory(uvDir);
        var exePath = Path.Combine(uvDir, "uv.exe");
        await File.WriteAllBytesAsync(exePath, Array.Empty<byte>());

        var installer = MakeInstaller(FakeUvZip());
        var result = await installer.InstallAsync(CancellationToken.None);
        Assert.True(File.Exists(result));
        Assert.NotEqual(0, new FileInfo(result).Length);
    }
}
