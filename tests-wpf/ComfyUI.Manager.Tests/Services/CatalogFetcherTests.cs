using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Moq;
using Moq.Protected;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class CatalogFetcherTests
{
    /// <summary>
    /// Build a mocked HttpClient whose single SendAsync call returns the given JSON body.
    /// </summary>
    private static HttpClient MockedHttpClient(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchAsync_ParsesValidJson_ReturnsEntries()
    {
        var json = @"[
            { ""id"": ""comfy-node-a"", ""author"": ""alice"", ""description"": ""node A"" },
            { ""id"": ""comfy-node-b"", ""author"": ""bob"" }
        ]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json), cacheTtlMinutes: 60);

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Equal(2, entries.Count);
        Assert.Equal("comfy-node-a", entries[0].Package);
        Assert.Equal("https://example/registry.json", entries[0].SourceUrl);
        Assert.Contains("alice", entries[0].RawMetadata.Values);
        Assert.Equal("comfy-node-b", entries[1].Package);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToName_WhenIdMissing()
    {
        var json = @"[{ ""name"": ""fallback-name"" }]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Single(entries);
        Assert.Equal("fallback-name", entries[0].Package);
    }

    [Fact]
    public async Task FetchAsync_SkipsRows_BothIdAndNameMissing()
    {
        var json = @"[
            { ""id"": ""keep-me"" },
            { ""unrelated"": ""field"" },
            { ""name"": ""also-keep"" }
        ]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Equal(2, entries.Count);
        Assert.Equal("keep-me", entries[0].Package);
        Assert.Equal("also-keep", entries[1].Package);
    }

    [Fact]
    public async Task FetchAsync_EmptyArray_ReturnsEmptyList()
    {
        var fetcher = new CatalogFetcher(MockedHttpClient("[]"));

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_NetworkFailure_Throws()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network down"));
        var fetcher = new CatalogFetcher(new HttpClient(handler.Object));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchAsync("https://example/registry.json"));
    }

    [Fact]
    public async Task FetchAsync_SetsExpiresAt_AccordingToTtl()
    {
        var json = @"[{ ""id"": ""pkg"" }]";
        var fetcher = new CatalogFetcher(MockedHttpClient(json), cacheTtlMinutes: 30);

        var entries = await fetcher.FetchAsync("https://example/registry.json");

        Assert.Single(entries);
        // CachedAt + 30min ≈ ExpiresAt
        var cached = DateTime.Parse(entries[0].CachedAt);
        var expires = DateTime.Parse(entries[0].ExpiresAt);
        var diff = expires - cached;
        Assert.InRange(diff.TotalMinutes, 29.5, 30.5);
    }
}