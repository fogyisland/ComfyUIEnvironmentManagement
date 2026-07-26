using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// Tests for <see cref="PyTorchVersionDirectory"/>.
///
/// Covers the cache -> fetch -> fallback chain plus entry-shaping invariants
/// (nightly at index 0, stable entries release-date descending,
/// <c>DisplayName</c> format, fallback never written to cache).
///
/// Uses in-memory fakes that subclass <see cref="PyTorchVersionCatalog"/>
/// and <see cref="PyTorchVersionCatalogCache"/> (with the relevant methods
/// marked <c>virtual</c>) so no real <c>HttpClient</c> is ever wired in.
/// </summary>
public sealed class PyTorchVersionDirectoryTests : IDisposable
{
    // Shared scratch dir so the cache base ctor's Path.Combine never blows
    // up. The fakes don't actually touch disk — this only satisfies the
    // base ctor argument contract.
    private readonly string _scratchDir;

    public PyTorchVersionDirectoryTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"pt-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true);
        }
        catch
        {
        }
    }

    // ----- Fakes -----

    /// <summary>
    /// In-memory catalog that records every <c>FetchAsync</c> call and
    /// returns whichever stable list was preset.
    /// </summary>
    private sealed class FakeCatalog : PyTorchVersionCatalog
    {
        private readonly IReadOnlyList<PyTorchVersion>? _toReturn;
        public int FetchCallCount { get; private set; }

        public FakeCatalog(IReadOnlyList<PyTorchVersion>? toReturn, string scratchDir)
            : base(http: null!)
        {
            // Touch the scratch dir so the linter doesn't flag the
            // otherwise-unused parameter (kept for symmetry with FakeCache
            // and the public base ctor contract).
            _ = scratchDir;
            _toReturn = toReturn;
        }

        public override Task<IReadOnlyList<PyTorchVersion>?> FetchAsync(CancellationToken ct = default)
        {
            FetchCallCount++;
            return Task.FromResult(_toReturn);
        }
    }

    /// <summary>
    /// In-memory cache that holds a preset list (or null when none is preset)
    /// and records every <see cref="WriteAsync"/> call (argument + count).
    /// </summary>
    private sealed class FakeCache : PyTorchVersionCatalogCache
    {
        private readonly IReadOnlyList<PyTorchVersion>? _preset;
        public int WriteCallCount { get; private set; }
        public IReadOnlyList<PyTorchVersion>? LastWritten { get; private set; }

        /// <summary>
        /// Drives the read side: when <paramref name="preset"/> is null it
        /// behaves as "no cache" (returns null); when non-null it returns
        /// the preset list verbatim.
        /// </summary>
        public FakeCache(IReadOnlyList<PyTorchVersion>? preset, string scratchDir)
            : base(appDataDir: scratchDir)
        {
            _preset = preset;
        }

        public override Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_preset);
        }

        public override Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default)
        {
            WriteCallCount++;
            LastWritten = versions;
            return Task.CompletedTask;
        }
    }

    // ----- Helpers -----

    private static PyTorchVersion V(
        string version,
        DateTimeOffset? releaseDate = null,
        IReadOnlyList<string>? cuda = null,
        bool hasCpu = true) => new()
    {
        Version = version,
        ReleaseDate = releaseDate ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        CudaVariants = cuda ?? new[] { "cu118", "cu121", "cu124", "cu126" },
        HasCpu = hasCpu,
    };

    // ----- Constructor -----

    [Fact]
    public void Ctor_NullCatalog_Throws()
    {
        var cache = new FakeCache(null, _scratchDir);
        Assert.Throws<ArgumentNullException>(() => new PyTorchVersionDirectory(null!, cache));
    }

    [Fact]
    public void Ctor_NullCache_Throws()
    {
        var catalog = new FakeCatalog(null, _scratchDir);
        Assert.Throws<ArgumentNullException>(() => new PyTorchVersionDirectory(catalog, null!));
    }

    // ----- Cache -> fetch -> fallback chain -----

    [Fact]
    public async Task GetAllAsync_CacheHit_DoesNotCallCatalog()
    {
        var cachedList = new List<PyTorchVersion> { V("2.13.0"), V("2.12.0") };
        var catalog = new FakeCatalog(null, _scratchDir);
        var cache = new FakeCache(cachedList, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        var result = await dir.GetAllAsync();

        Assert.Equal(0, catalog.FetchCallCount);
        Assert.NotNull(result);
        // Nightly first, then the two cached stables (already in cached order).
        Assert.True(result.Count >= 2);
        Assert.True(result[0].IsNightly);
        Assert.Equal("nightly", result[0].Version);
    }

    [Fact]
    public async Task GetAllAsync_CacheMiss_CallsCatalogAndWritesCache()
    {
        var catalogList = new List<PyTorchVersion>
        {
            V("2.13.0", releaseDate: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            V("2.12.0", releaseDate: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        var catalog = new FakeCatalog(catalogList, _scratchDir);
        var cache = new FakeCache(preset: null, _scratchDir); // miss

        var dir = new PyTorchVersionDirectory(catalog, cache);

        var result = await dir.GetAllAsync();

        Assert.Equal(1, catalog.FetchCallCount);
        Assert.Equal(1, cache.WriteCallCount);
        Assert.Same(catalogList, cache.LastWritten);
        // Nightly first, then catalog entries (already release-date desc).
        Assert.True(result[0].IsNightly);
        Assert.Equal(3, result.Count);
        Assert.Equal("2.13.0", result[1].Version);
        Assert.Equal("2.12.0", result[2].Version);
    }

    [Fact]
    public async Task GetAllAsync_CatalogFetchFailsWithNoCache_ReturnsFallback()
    {
        var catalog = new FakeCatalog(toReturn: null, _scratchDir);
        var cache = new FakeCache(preset: null, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        var result = await dir.GetAllAsync();

        // Fallback: at least 2 entries (nightly + 2.13.0 stable).
        Assert.True(result.Count >= 2);
        Assert.True(result[0].IsNightly);
        Assert.Equal("nightly", result[0].Version);
        Assert.Equal("PyTorch Nightly", result[0].DisplayName);
        // 2.13.0 stable must be present in the fallback list.
        var stable213 = result.FirstOrDefault(e => !e.IsNightly && e.Version == "2.13.0");
        Assert.NotNull(stable213);
        Assert.NotNull(stable213!.StableMetadata);
        Assert.Equal(new[] { "cu118", "cu121", "cu124", "cu126" }, stable213.StableMetadata!.CudaVariants);
        Assert.True(stable213.StableMetadata.HasCpu);
    }

    [Fact]
    public async Task GetAllAsync_NightlyAlwaysFirst()
    {
        var catalogList = new List<PyTorchVersion>
        {
            V("2.13.0", releaseDate: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            V("2.12.0", releaseDate: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        var catalog = new FakeCatalog(catalogList, _scratchDir);
        var cache = new FakeCache(preset: null, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        var result = await dir.GetAllAsync();

        Assert.True(result[0].IsNightly);
        Assert.Equal("nightly", result[0].Version);
        Assert.Equal("PyTorch Nightly", result[0].DisplayName);
        Assert.Null(result[0].StableMetadata);
    }

    [Fact]
    public async Task GetAllAsync_StableEntriesOrderedByReleaseDateDesc()
    {
        // Feed versions out of order; directory must emit release-date desc.
        var catalogList = new List<PyTorchVersion>
        {
            V("2.10.0", releaseDate: new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            V("2.13.0", releaseDate: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            V("2.11.0", releaseDate: new DateTimeOffset(2025, 11, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        var catalog = new FakeCatalog(catalogList, _scratchDir);
        var cache = new FakeCache(preset: null, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        var result = await dir.GetAllAsync();

        var stables = result.Where(e => !e.IsNightly).Select(e => e.Version).ToList();
        Assert.Equal(new[] { "2.13.0", "2.11.0", "2.10.0" }, stables);
    }

    [Fact]
    public async Task GetAllAsync_FallbackNotWrittenToCache()
    {
        var catalog = new FakeCatalog(toReturn: null, _scratchDir);
        var cache = new FakeCache(preset: null, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        await dir.GetAllAsync();

        Assert.Equal(0, cache.WriteCallCount);
        Assert.Null(cache.LastWritten);
    }

    [Fact]
    public async Task GetAllAsync_EmptyCache_TreatedAsMiss()
    {
        // Empty list (not null) → cache miss, catalog must be called.
        var emptyList = new List<PyTorchVersion>();
        var catalog = new FakeCatalog(toReturn: new List<PyTorchVersion>
        {
            V("2.13.0", releaseDate: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
        }, _scratchDir);
        var cache = new FakeCache(preset: emptyList, _scratchDir);

        var dir = new PyTorchVersionDirectory(catalog, cache);

        await dir.GetAllAsync();

        Assert.Equal(1, catalog.FetchCallCount);
        Assert.Equal(1, cache.WriteCallCount);
    }
}
