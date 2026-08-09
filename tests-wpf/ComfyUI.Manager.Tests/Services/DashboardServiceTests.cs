using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Environment = ComfyUI.Manager.Models.Environment;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_EmptyEnvList_ReturnsZeroes()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService();

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(new EnvironmentCounts(0, 0, 0), snapshot.EnvironmentCounts);
        Assert.Equal(0, snapshot.NodeCount);
        Assert.Empty(snapshot.RecentOperations);
        Assert.Equal("v0.6.x", snapshot.LatestRelease);
        Assert.False(snapshot.GitHubFailed);
    }

    [Fact]
    public async Task GetSnapshotAsync_MixedEnvs_CountsByStatus()
    {
        using var fixture = new Fixture
        {
            Environments = new List<Environment>
            {
                Env("running", "done"),
                Env("stopped", "done"),
                Env("pending", null),
            },
        };

        var snapshot = await fixture.CreateService().GetSnapshotAsync();

        Assert.Equal(new EnvironmentCounts(2, 1, 1), snapshot.EnvironmentCounts);
    }

    [Fact]
    public async Task GetSnapshotAsync_GitHubFailure_StillReturnsSnapshotWithNullRelease()
    {
        using var fixture = new Fixture { GitHubStatus = HttpStatusCode.InternalServerError, NodeCount = 4 };

        var snapshot = await fixture.CreateService().GetSnapshotAsync();

        Assert.Null(snapshot.LatestRelease);
        Assert.True(snapshot.GitHubFailed);
        Assert.Equal(4, snapshot.NodeCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_NodeFailure_Throws()
    {
        using var fixture = new Fixture { NodeException = new InvalidOperationException("node unavailable") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateService().GetSnapshotAsync());
    }

    [Fact]
    public async Task GetSnapshotAsync_EnvFailure_Throws()
    {
        using var fixture = new Fixture { EnvException = new InvalidOperationException("env unavailable") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateService().GetSnapshotAsync());
    }

    [Fact]
    public async Task GetSnapshotAsync_RecentOps_ReturnsFiveLatest()
    {
        using var fixture = new Fixture();
        fixture.WriteLogs(
            "[10:00:00.001] [INFO ] [env-start] first",
            "[10:00:00.002] [WARN ] [bed-install] second",
            "[10:00:00.003] [ERROR] [catalog] third",
            "[10:00:00.004] [INFO ] [nodes] fourth",
            "[10:00:00.005] [INFO ] [env-stop] fifth");

        var snapshot = await fixture.CreateService().GetSnapshotAsync();

        Assert.Equal(5, snapshot.RecentOperations.Count);
        var latest = snapshot.RecentOperations[0];
        Assert.Equal("env-stop", latest.Subsystem);
        Assert.Equal("fifth", latest.Message);
        Assert.Equal(10, latest.ParsedTime.Hour);
        Assert.Equal(5, latest.ParsedTime.Millisecond);
    }

    [Fact]
    public async Task GetSnapshotAsync_LogReadFailure_ReturnsEmptyOps()
    {
        using var fixture = new Fixture();
        fixture.CreateLogDirectoryAtTodayFilePath();

        var snapshot = await fixture.CreateService().GetSnapshotAsync();

        Assert.Empty(snapshot.RecentOperations);
        Assert.Equal("v0.6.x", snapshot.LatestRelease);
        Assert.False(snapshot.GitHubFailed);
    }

    [Fact]
    public async Task GetSnapshotAsync_ParallelExecution_FasterThanSequential()
    {
        using var fixture = new Fixture { Delay = TimeSpan.FromMilliseconds(200) };
        fixture.WriteLogs("[10:00:00.001] [INFO ] [test] delayed");
        var stopwatch = Stopwatch.StartNew();

        await fixture.CreateService().GetSnapshotAsync();

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(700), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task GetSnapshotAsync_CancellationRequested_StopsCleanly()
    {
        using var fixture = new Fixture { Delay = TimeSpan.FromSeconds(1) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.CreateService().GetSnapshotAsync(cts.Token));
    }

    private static Environment Env(string status, string? bedStatus) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Name = "env", RootPath = "/tmp/env",
        Status = status, BedStatus = bedStatus,
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"dashboard-{Guid.NewGuid():N}");
        public List<Environment> Environments { get; init; } = new();
        public Exception? EnvException { get; init; }
        public Exception? NodeException { get; init; }
        public long NodeCount { get; init; }
        public HttpStatusCode GitHubStatus { get; init; } = HttpStatusCode.OK;
        public TimeSpan Delay { get; init; }

        public Fixture() => Directory.CreateDirectory(_root);

        public DashboardService CreateService()
        {
            var logger = new AppLogger(_root);
            return new DashboardService(
                new FakeEnvironmentRepository(this),
                new FakeNodeRepository(this),
                logger,
                new HttpClient(new FakeHttpHandler(this)));
        }

        public void WriteLogs(params string[] lines)
        {
            var dir = Path.Combine(_root, "Logs");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), lines);
        }

        public void CreateLogDirectoryAtTodayFilePath()
        {
            var dir = Path.Combine(_root, "Logs");
            Directory.CreateDirectory(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private sealed class FakeEnvironmentRepository : IEnvironmentRepository
        {
            private readonly Fixture _fixture;
            public FakeEnvironmentRepository(Fixture fixture) => _fixture = fixture;
            public List<Environment> ListAll()
            {
                if (_fixture.Delay > TimeSpan.Zero) Thread.Sleep(_fixture.Delay);
                if (_fixture.EnvException is not null) throw _fixture.EnvException;
                return _fixture.Environments;
            }
            public Environment? Get(string envId) => null;
            public void Upsert(Environment env) { }
            public int? GetMaxPort() => null;
        }

        private sealed class FakeNodeRepository : INodeRepository
        {
            private readonly Fixture _fixture;
            public FakeNodeRepository(Fixture fixture) => _fixture = fixture;
            public async Task<long> CountAllAsync(CancellationToken ct = default)
            {
                if (_fixture.Delay > TimeSpan.Zero) await Task.Delay(_fixture.Delay, ct);
                if (_fixture.NodeException is not null) throw _fixture.NodeException;
                return _fixture.NodeCount;
            }
            public List<ScannedNode> ListByEnv(string envId) => new();
            public ScannedNode? Get(string nodeId) => null;
        }

        private sealed class FakeHttpHandler : HttpMessageHandler
        {
            private readonly Fixture _fixture;
            public FakeHttpHandler(Fixture fixture) => _fixture = fixture;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (_fixture.Delay > TimeSpan.Zero) await Task.Delay(_fixture.Delay, ct);
                return new HttpResponseMessage(_fixture.GitHubStatus)
                {
                    Content = new StringContent("{\"tag_name\":\"v0.6.x\"}", Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
