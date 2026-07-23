using System;
using System.Text.Json;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// Tests for <see cref="PyTorchLiveVersions"/> POCO:
/// - default values are sensible for a fresh instance
/// - JSON round-trip preserves all fields (Stable, HasNightlyCu126, FetchedAt)
/// - deserialization of a hand-crafted JSON string (what the cache file will
///   contain) produces the same field values
/// </summary>
public sealed class PyTorchLiveVersionsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Defaults_AreSensibleForFreshInstance()
    {
        var v = new PyTorchLiveVersions();

        // Stable defaults to empty string (placeholder; fetcher overwrites)
        Assert.Equal("", v.Stable);
        // HasNightlyCu126 defaults to true — fetcher sets false only on parse miss
        Assert.True(v.HasNightlyCu126);
        // FetchedAt is a struct default (MinValue UTC) — fetcher overwrites
        Assert.Equal(DateTimeOffset.MinValue, v.FetchedAt);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var fetchedAt = new DateTimeOffset(2026, 7, 24, 12, 30, 45, TimeSpan.Zero);
        var original = new PyTorchLiveVersions
        {
            Stable = "2.13.0",
            HasNightlyCu126 = true,
            FetchedAt = fetchedAt,
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PyTorchLiveVersions>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("2.13.0", deserialized!.Stable);
        Assert.True(deserialized.HasNightlyCu126);
        Assert.Equal(fetchedAt, deserialized.FetchedAt);
    }

    [Fact]
    public void Deserialize_FromCacheFileShape_PopulatesFields()
    {
        // Hand-crafted JSON matching what PyTorchVersionCache will write to
        // %APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json
        var json = """{"stable":"2.13.0","hasNightlyCu126":true,"fetchedAt":"2026-07-24T12:30:45+00:00"}""";

        var v = JsonSerializer.Deserialize<PyTorchLiveVersions>(json, JsonOptions);

        Assert.NotNull(v);
        Assert.Equal("2.13.0", v!.Stable);
        Assert.True(v.HasNightlyCu126);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 24, 12, 30, 45, TimeSpan.Zero),
            v.FetchedAt);
    }

    [Fact]
    public void Deserialize_HasNightlyCu126False_RoundTripsFalse()
    {
        // Defensive: if fetcher ever sets HasNightlyCu126 = false, round-trip must
        // not silently flip it to the default (true).
        var original = new PyTorchLiveVersions
        {
            Stable = "2.13.0",
            HasNightlyCu126 = false,
            FetchedAt = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PyTorchLiveVersions>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.False(deserialized!.HasNightlyCu126);
    }
}