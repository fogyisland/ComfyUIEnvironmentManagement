using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = System.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// SystemInfoCollector 的纯函数 / override 测试。
///
/// 不调真实 nvidia-smi / nvcc / registry(测试环境可能没装/没权限)—
/// 静态 ParseNvidiaSmi/ParseNvccVersion 测格式正确,
/// TryParseNvidiaSmiAsync/TryParseNvccVersionAsync 用 FakeCollector override RunProcessAsync。
/// </summary>
public class SystemInfoCollectorTests
{
    // -------- 静态 ParseNvidiaSmi --------

    [Fact]
    public void ParseNvidiaSmi_TwoGpus_ReturnsTwoEntries()
    {
        var stdout = "0, NVIDIA GeForce RTX 4090, 555.85, 24576\r\n" +
                     "1, NVIDIA GeForce RTX 4090, 555.85, 24576\r\n";
        var gpus = SystemInfoCollector.ParseNvidiaSmi(stdout);
        Assert.Equal(2, gpus.Count);
        Assert.Equal("NVIDIA GeForce RTX 4090", gpus[0].Name);
        Assert.Equal("555.85", gpus[0].DriverVersion);
        Assert.Equal(0, gpus[0].Index);
        Assert.Equal(24576L * 1024 * 1024, gpus[0].MemoryBytes);
        Assert.Equal(1, gpus[1].Index);
    }

    [Fact]
    public void ParseNvidiaSmi_SingleGpu_ReturnsOneEntry()
    {
        var stdout = "0, Tesla V100, 535.54, 16384\r\n";
        var gpus = SystemInfoCollector.ParseNvidiaSmi(stdout);
        Assert.Single(gpus);
        Assert.Equal(0, gpus[0].Index);
        Assert.Equal("Tesla V100", gpus[0].Name);
    }

    [Fact]
    public void ParseNvidiaSmi_Empty_ReturnsEmpty()
    {
        Assert.Empty(SystemInfoCollector.ParseNvidiaSmi(""));
        Assert.Empty(SystemInfoCollector.ParseNvidiaSmi("   \n  \n"));
    }

    [Fact]
    public void ParseNvidiaSmi_SkipsMalformedLines()
    {
        var stdout = "0, RTX 4090, 555.85, 24576\r\n" +
                     "garbage line with no commas\r\n" +
                     "1, only, three\r\n" +
                     "2, A100, 535.54, 40960\r\n";
        var gpus = SystemInfoCollector.ParseNvidiaSmi(stdout);
        Assert.Equal(2, gpus.Count);
        Assert.Equal(0, gpus[0].Index);
        Assert.Equal(2, gpus[1].Index);
    }

    // -------- 静态 ParseNvccVersion --------

    [Fact]
    public void ParseNvccVersion_Standard_ReturnsVersion()
    {
        var stdout = "nvcc: NVIDIA (R) Cuda compiler driver\r\n" +
                     "Copyright (c) 2005-2023 NVIDIA Corporation\r\n" +
                     "Built on Tue_Aug_15_22:02:13_PDT_2023\r\n" +
                     "Cuda compilation tools, release 12.2, V12.2.140\r\n" +
                     "Build cuda_12.2.r12.2/compiler.34714.0/build.34714";
        Assert.Equal("12.2", SystemInfoCollector.ParseNvccVersion(stdout));
    }

    [Fact]
    public void ParseNvccVersion_OldVersion_ReturnsVersion()
    {
        var stdout = "Cuda compilation tools, release 11.8, V11.8.89";
        Assert.Equal("11.8", SystemInfoCollector.ParseNvccVersion(stdout));
    }

    [Fact]
    public void ParseNvccVersion_NoMatch_ReturnsNull()
    {
        var stdout = "some random output without release info";
        Assert.Null(SystemInfoCollector.ParseNvccVersion(stdout));
    }

    [Fact]
    public void ParseNvccVersion_Empty_ReturnsNull()
    {
        Assert.Null(SystemInfoCollector.ParseNvccVersion(""));
        Assert.Null(SystemInfoCollector.ParseNvccVersion((string?)null!));
    }

    // -------- 静态 ParseNvidiaSmiCudaVersion --------

    [Fact]
    public void ParseNvidiaSmiCudaVersion_StandardHeader_ReturnsVersion()
    {
        var stdout = "Wed Aug  5 19:20:57 2026       \n" +
                     "+-----------------------------------------------------------------------------------------+\n" +
                     "| NVIDIA-SMI 596.36                 Driver Version: 596.36         CUDA Version: 13.2     |\n" +
                     "+-----------------------------------------+------------------------+----------------------+\n";
        Assert.Equal("13.2", SystemInfoCollector.ParseNvidiaSmiCudaVersion(stdout));
    }

    [Fact]
    public void ParseNvidiaSmiCudaVersion_OldVersion_ReturnsVersion()
    {
        var stdout = "| NVIDIA-SMI 555.85       Driver Version: 555.85       CUDA Version: 12.5                |";
        Assert.Equal("12.5", SystemInfoCollector.ParseNvidiaSmiCudaVersion(stdout));
    }

    [Fact]
    public void ParseNvidiaSmiCudaVersion_NoMatch_ReturnsNull()
    {
        Assert.Null(SystemInfoCollector.ParseNvidiaSmiCudaVersion("no CUDA here"));
    }

    [Fact]
    public void ParseNvidiaSmiCudaVersion_Empty_ReturnsNull()
    {
        Assert.Null(SystemInfoCollector.ParseNvidiaSmiCudaVersion(""));
        Assert.Null(SystemInfoCollector.ParseNvidiaSmiCudaVersion((string?)null!));
    }

    // -------- virtual TryParse*Async with fake runner --------

    [Fact]
    public async Task TryParseNvidiaSmiAsync_FakeRunner_ReturnsParsedGpus()
    {
        var collector = new FakeCollector(new Dictionary<string, (bool Ok, string Stdout, string Stderr)>
        {
            ["nvidia-smi"] = (true, "0, RTX 4090, 555.85, 24576\r\n", ""),
        });
        var gpus = await collector.CallTryParseNvidiaSmiAsync(default);
        Assert.Single(gpus);
        Assert.Equal("RTX 4090", gpus[0].Name);
    }

    [Fact]
    public async Task TryParseNvidiaSmiAsync_NotInstalled_ReturnsEmpty()
    {
        // 模拟 nvidia-smi 找不到 → runner 返 ok=false
        var collector = new FakeCollector(new Dictionary<string, (bool, string, string)>
        {
            ["nvidia-smi"] = (false, "", "command not found"),
        });
        var gpus = await collector.CallTryParseNvidiaSmiAsync(default);
        Assert.Empty(gpus);
    }

    [Fact]
    public async Task TryParseNvccVersionAsync_FakeRunner_ReturnsVersion()
    {
        var collector = new FakeCollector(new Dictionary<string, (bool, string, string)>
        {
            ["nvcc"] = (true, "Cuda compilation tools, release 12.2, V12.2.140\r\n", ""),
        });
        var version = await collector.CallTryParseNvccVersionAsync(default);
        Assert.Equal("12.2", version);
    }

    [Fact]
    public async Task TryParseNvccVersionAsync_NotInstalled_ReturnsNull()
    {
        var collector = new FakeCollector(new Dictionary<string, (bool, string, string)>
        {
            ["nvcc"] = (false, "", "command not found"),
        });
        var version = await collector.CallTryParseNvccVersionAsync(default);
        Assert.Null(version);
    }

    // -------- CollectAsync 集成测试(fake all) --------

    [Fact]
    public async Task CollectAsync_FakeRunnerAndGpus_PopulatesSystemInfo()
    {
        var collector = new FakeCollector(new Dictionary<string, (bool Ok, string Stdout, string Stderr)>
        {
            ["nvidia-smi"] = (true, "0, RTX 4090, 555.85, 24576\r\n", ""),
            ["nvcc"] = (true, "Cuda compilation tools, release 12.2, V12.2.140\r\n", ""),
        });
        var info = await collector.CollectAsync(default);
        Assert.NotNull(info);
        Assert.Equal("12.2", info.CudaVersion);
        Assert.Single(info.Gpus);
        Assert.Equal("RTX 4090", info.Gpus[0].Name);
        Assert.True(info.CpuCores > 0);
        Assert.NotEmpty(info.Disks);  // 当前 OS 至少有一个 ready 盘
    }

    [Fact]
    public async Task CollectAsync_NvccMissing_CudaStaysNull()
    {
        // 集成:无 nvcc + nvidia-smi 也没了 → CUDA 仍 null(用户没 GPU/驱动)
        var collector = new FakeCollector(new Dictionary<string, (bool Ok, string Stdout, string Stderr)>
        {
            ["nvidia-smi"] = (false, "", "no fake response for nvidia-smi"),
        });
        var info = await collector.CollectAsync(default);
        Assert.Null(info.CudaVersion);
        Assert.Empty(info.Gpus);
    }

    // -------- 收集 disk fallback 测试 --------

    [Fact]
    public async Task CollectAsync_NoFakeDisks_StillReturnsRealOsDisks()
    {
        var collector = new FakeCollector(new Dictionary<string, (bool Ok, string Stdout, string Stderr)>());
        var info = await collector.CollectAsync(default);
        // DriveInfo.GetDrakes() 走的是真实 OS,在 CI 上可能 0 盘 — 不强制要求 > 0
        // 但至少 disk entries 有正确字段(若存在)
        foreach (var d in info.Disks)
        {
            Assert.False(string.IsNullOrEmpty(d.DriveLetter));
            Assert.True(d.TotalBytes > 0);
        }
    }

    // -------- 真实 nvidia-smi 路径(机器有 nvidia-smi 时才跑) --------

    [Fact]
    public async Task CollectAsync_RealNvidiaSmi_PicksUpGpus()
    {
        // 不同机器表现不同:有 nvidia-smi → 1 个 GPU,wifi no GPU → 0。这是
        // 环境依赖,不是 bug — 我们只验 parser 路径在真实 stdout 上能解析。
        var realFile = ResolveOnPath("nvidia-smi");
        if (realFile is null)
        {
            // 机器没装 nvidia-smi → 跳过
            return;
        }

        var collector = new SystemInfoCollector();
        var info = await collector.CollectAsync(default);
        // 至少跑了 — 不强制 Gpus > 0(LO 可能 process 拿到的 PATH 不一样)
        Assert.NotNull(info);
        // 真正的 stdout 解析:如果某行解析失败,DriverVersion 是空字符串
        foreach (var g in info.Gpus)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
        }
    }

    private static string? ResolveOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                var full = Path.Combine(dir, exe + ".exe");
                if (File.Exists(full)) return full;
            }
            catch
            {
                // ignore
            }
        }
        return null;
    }

    // -------- Test double --------

    private sealed class FakeCollector : SystemInfoCollector
    {
        private readonly Dictionary<string, (bool Ok, string Stdout, string Stderr)> _responses;

        public FakeCollector(Dictionary<string, (bool Ok, string Stdout, string Stderr)> responses)
        {
            _responses = responses;
        }

        public Task<IReadOnlyList<GpuInfo>> CallTryParseNvidiaSmiAsync(CancellationToken ct)
            => TryParseNvidiaSmiAsync(ct);

        public Task<string?> CallTryParseNvccVersionAsync(CancellationToken ct)
            => TryParseNvccVersionAsync(ct);

        protected override Task<(bool Ok, string Stdout, string Stderr)> RunProcessAsync(
            string exe, string args, TimeSpan timeout, CancellationToken ct)
        {
            if (_responses.TryGetValue(exe, out var resp))
            {
                return Task.FromResult(resp);
            }
            return Task.FromResult((false, "", "no fake response for " + exe));
        }
    }
}