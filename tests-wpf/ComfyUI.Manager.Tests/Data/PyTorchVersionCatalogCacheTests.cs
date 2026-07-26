using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

public sealed class PyTorchVersionCatalogCacheTests
{
    [Fact]
    public async Task TryRead_ReturnsNullWhenFileMissing()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCatalogCache(dir);

            var result = await cache.TryReadAsync();

            Assert.Null(result);
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
            var cache = new PyTorchVersionCatalogCache(dir);
            var original = CreateCatalog();
            await cache.WriteAsync(original);

            var result = await cache.TryReadAsync();

            Assert.NotNull(result);
            Assert.Equal(original.Count, result!.Count);
            Assert.Equal(original[0].Version, result[0].Version);
            Assert.Equal(original[0].ReleaseDate, result[0].ReleaseDate);
            Assert.Equal(original[0].CudaVariants, result[0].CudaVariants);
            Assert.Equal(original[0].HasCpu, result[0].HasCpu);
            Assert.Equal(original[1].Version, result[1].Version);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task TryRead_ReturnsEntryWithOldReleaseDate_NoTtl()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCatalogCache(dir);
            var old = new List<PyTorchVersion>
            {
                new()
                {
                    Version = "1.0.0",
                    ReleaseDate = new DateTimeOffset(2017, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    CudaVariants = new[] { "cu118" },
                    HasCpu = true,
                },
            };
            await cache.WriteAsync(old);

            var result = await cache.TryReadAsync();

            Assert.NotNull(result);
            Assert.Single(result!);
            Assert.Equal("1.0.0", result[0].Version);
            Assert.Equal(old[0].ReleaseDate, result[0].ReleaseDate);
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
            var cache = new PyTorchVersionCatalogCache(dir);
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
            var cache = new PyTorchVersionCatalogCache(cacheDir);

            await cache.WriteAsync(CreateCatalog());

            Assert.True(File.Exists(cache.FilePath));
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
        var cache = new PyTorchVersionCatalogCache(invalidPath);

        var exception = await Record.ExceptionAsync(() => cache.WriteAsync(CreateCatalog()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Write_ToPathInsideFile_DoesNotThrow()
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "blocker");
            await File.WriteAllTextAsync(filePath, "i am a file, not a directory");

            // Treat the existing file as a "directory" — CreateDirectory / write must fail internally.
            var cache = new PyTorchVersionCatalogCache(filePath);

            var exception = await Record.ExceptionAsync(() => cache.WriteAsync(CreateCatalog()));

            Assert.Null(exception);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task TryRead_HonorsCancellation_ReturnsNull()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCatalogCache(dir);
            await cache.WriteAsync(CreateCatalog());

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The token is propagated to File.ReadAllTextAsync; any resulting
            // OperationCanceledException is swallowed and surfaces as null (never thrown).
            var result = await cache.TryReadAsync(cts.Token);

            Assert.Null(result);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Write_PropagatesCancellation()
    {
        var dir = CreateTempDir();
        try
        {
            var cache = new PyTorchVersionCatalogCache(dir);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // WriteAsync swallows all exceptions including cancellation; it must not throw
            // and must not produce a file when cancelled before the write completes.
            var exception = await Record.ExceptionAsync(
                () => cache.WriteAsync(CreateCatalog(), cts.Token));

            Assert.Null(exception);
            Assert.False(File.Exists(cache.FilePath));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private static List<PyTorchVersion> CreateCatalog() => new()
    {
        new()
        {
            Version = "2.13.0",
            ReleaseDate = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
            CudaVariants = new[] { "cu118", "cu121", "cu126" },
            HasCpu = true,
        },
        new()
        {
            Version = "2.12.0",
            ReleaseDate = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
            CudaVariants = new[] { "cu118", "cu124" },
            HasCpu = false,
        },
    };

    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
