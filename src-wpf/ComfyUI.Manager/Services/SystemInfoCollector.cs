using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Microsoft.Win32;
using Environment = System.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// SystemInfoCollector:一次性收集系统状态面板所需的所有字段。
///
/// 数据来源(按字段):
/// - OS:Environment.OSVersion + 注册表 ProductName / DisplayVersion / CurrentBuild
/// - CPU:Environment.ProcessorCount + 注册表 CentralProcessor\0\ProcessorNameString
/// - Memory:Microsoft.VisualBasic.Devices.ComputerInfo (Total/AvailablePhysicalMemory)
/// - Disks:System.IO.DriveInfo.GetDrives().Where(Ready)
/// - GPU:`nvidia-smi --query-gpu=... --format=csv,noheader,nounits`(Process.Start,异步)
/// - CUDA:`nvcc --version`(Process.Start,异步,正则 `release (\d+\.\d+)`)
///
/// 失败语义:nvidia-smi/nvcc 找不到 → Gpus=[] / CudaVersion=null(不抛异常);
/// 其他字段读不到就 fallback 到 "?" 或 Environment.OSVersion.VersionString。
///
/// 测试 seam:`RunProcessAsync` 是 virtual,测试可 override 注入 fake stdout。
/// 解析方法(私有)是 static 也可单独测。
/// </summary>
public class SystemInfoCollector
{
    private static readonly Regex CudaVersionPattern = new(
        @"release\s+(?<v>\d+\.\d+)",
        RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public async Task<SystemInfo> CollectAsync(CancellationToken ct = default)
    {
        var osVersion = Environment.OSVersion.VersionString;
        var osBuild = GetWindowsProductName() ?? osVersion;

        var cpuName = GetCpuName();
        var cpuCores = Environment.ProcessorCount;

        long totalMem = 0, availMem = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ms))
        {
            totalMem = (long)ms.ullTotalPhys;
            availMem = (long)ms.ullAvailPhys;
        }

        var disks = CollectDisks();
        var (gpus, cuda) = await CollectGpuAndCudaAsync(ct);

        return new SystemInfo(
            OsVersion: osVersion,
            OsBuild: osBuild,
            CpuName: cpuName,
            CpuCores: cpuCores,
            TotalMemoryBytes: totalMem,
            AvailableMemoryBytes: availMem,
            Disks: disks,
            Gpus: gpus,
            CudaVersion: cuda,
            CollectedAt: DateTime.Now);
    }

    private static string? GetWindowsProductName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;
            var product = key.GetValue("ProductName") as string;
            var displayVer = key.GetValue("DisplayVersion") as string;
            var build = key.GetValue("CurrentBuild") as string;
            if (product is null) return null;
            return displayVer is not null
                ? $"{product} {displayVer} (build {build ?? "?"})"
                : $"{product} (build {build ?? "?"})";
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<DiskInfo> CollectDisks()
    {
        var result = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            try
            {
                result.Add(new DiskInfo(
                    DriveLetter: d.Name,
                    TotalBytes: d.TotalSize,
                    AvailableBytes: d.AvailableFreeSpace));
            }
            catch
            {
                // 单个盘读不到(权限/网络盘) → 跳过
            }
        }
        return result;
    }

    private async Task<(IReadOnlyList<GpuInfo> Gpus, string? CudaVersion)> CollectGpuAndCudaAsync(
        CancellationToken ct)
    {
        var gpus = await TryParseNvidiaSmiAsync(ct);
        var cuda = await TryParseNvccVersionAsync(ct);
        return (gpus, cuda);
    }

    /// <summary>
    /// 跑 `nvidia-smi --query-gpu=index,name,driver_version,memory.total --format=csv,noheader,nounits`。
    /// 没装 / 退出非零 → 返空 list(不抛)。
    /// </summary>
    protected virtual async Task<IReadOnlyList<GpuInfo>> TryParseNvidiaSmiAsync(CancellationToken ct)
    {
        var (ok, stdout, _) = await RunProcessAsync(
            "nvidia-smi",
            "--query-gpu=index,name,driver_version,memory.total --format=csv,noheader,nounits",
            TimeSpan.FromSeconds(5), ct);
        if (!ok || string.IsNullOrWhiteSpace(stdout))
        {
            return Array.Empty<GpuInfo>();
        }
        return ParseNvidiaSmi(stdout);
    }

    /// <summary>
    /// 跑 `nvcc --version`,提取 `release X.Y` 段。找不到 → null。
    /// </summary>
    protected virtual async Task<string?> TryParseNvccVersionAsync(CancellationToken ct)
    {
        var (ok, stdout, _) = await RunProcessAsync(
            "nvcc", "--version", TimeSpan.FromSeconds(5), ct);
        if (!ok || string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }
        return ParseNvccVersion(stdout);
    }

    /// <summary>
    /// 跑外部命令,捕获 stdout/stderr/exitCode。默认实现:Process.Start + 重定向。
    /// 测试 override 注入 fake 输出。
    /// </summary>
    protected virtual async Task<(bool Ok, string Stdout, string Stderr)> RunProcessAsync(
        string exe, string args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "Process.Start 返回 null");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                return (false, "", "timeout");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (p.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }

    // -------- 解析方法(static,单测) --------

    /// <summary>
    /// 解析 nvidia-smi CSV(每行: index, name, driver_version, memory_mib)。
    /// 空白行 / 字段不够 → 跳过。
    /// </summary>
    public static List<GpuInfo> ParseNvidiaSmi(string stdout)
    {
        var result = new List<GpuInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return result;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0].Trim(), out var idx)) continue;
            var name = parts[1].Trim();
            var driver = parts[2].Trim();
            long memBytes = 0;
            if (long.TryParse(parts[3].Trim(), out var memMib))
            {
                memBytes = memMib * 1024L * 1024L;
            }
            result.Add(new GpuInfo(idx, name, driver, memBytes));
        }
        return result;
    }

    /// <summary>
    /// 解析 nvcc --version 输出,找 `release X.Y` 段。返回 version 字符串或 null。
    /// </summary>
    public static string? ParseNvccVersion(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var m = CudaVersionPattern.Match(stdout);
        return m.Success ? m.Groups["v"].Value : null;
    }
}