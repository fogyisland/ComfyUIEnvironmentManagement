using System;
using System.IO;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class PyTorchVersionCacheTests
{
    [Fact]
    public async Task TryRead_ReturnsNullWhenFileMissing()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCache(dir);

            var result = await cache.TryReadAsync();

            Assert.Null(result);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task TryRead_ReturnsParsedWhenWithinTtl()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCache(dir);
            var fresh = new PyTorchLiveVersions
            {
                Stable = "2.13.0",
                HasNightlyCu126 = true,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            await cache.WriteAsync(fresh);

            var result = await cache.TryReadAsync();

            Assert.NotNull(result);
            Assert.Equal(fresh.Stable, result!.Stable);
            Assert.Equal(fresh.HasNightlyCu126, result.HasNightlyCu126);
            Assert.Equal(fresh.FetchedAt, result.FetchedAt);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task TryRead_ReturnsNullWhenTtlExpired()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCache(dir);
            await cache.WriteAsync(new PyTorchLiveVersions
            {
                Stable = "2.13.0",
                HasNightlyCu126 = true,
                FetchedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            });

            var result = await cache.TryReadAsync();

            Assert.Null(result);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task TryRead_ReturnsNullOnCorruptJson()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCache(dir);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(cache.FilePath, "not valid json");

            var result = await cache.TryReadAsync();

            Assert.Null(result);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Write_CreatesDirectoryIfMissing()
    {
        var dir = CreateTempDir();
        try
        {
            var cacheDir = Path.Combine(dir, "nested");
            var cache = new PyTorchVersionCache(cacheDir);
            await cache.WriteAsync(CreateFreshVersions());

            Assert.True(File.Exists(cache.FilePath));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Write_RoundTrip()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCache(dir);
            var original = CreateFreshVersions();
            await cache.WriteAsync(original);

            var result = await cache.TryReadAsync();

            Assert.NotNull(result);
            Assert.Equal(original.Stable, result!.Stable);
            Assert.Equal(original.HasNightlyCu126, result.HasNightlyCu126);
            Assert.Equal(original.FetchedAt, result.FetchedAt);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Write_DoesNotThrow_OnFailure()
    {
        var invalidPath = Path.Combine(Path.GetTempPath(), "invalid\0path");
        var cache = new PyTorchVersionCache(invalidPath);

        var exception = await Record.ExceptionAsync(() => cache.WriteAsync(CreateFreshVersions()));

        Assert.Null(exception);
    }

    private static PyTorchLiveVersions CreateFreshVersions() => new()
    {
        Stable = "2.13.0",
        HasNightlyCu126 = true,
        FetchedAt = DateTimeOffset.UtcNow,
    };

    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
