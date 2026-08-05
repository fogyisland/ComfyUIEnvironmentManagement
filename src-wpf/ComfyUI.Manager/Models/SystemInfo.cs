using System;
using System.Collections.Generic;

namespace ComfyUI.Manager.Models;

/// <summary>
/// 系统状态面板一次性快照:OS / CPU / 内存 / 磁盘 / GPU / CUDA。
/// 由 <see cref="Services.SystemInfoCollector"/> 在用户进入 tab 时异步收集。
/// </summary>
public sealed record SystemInfo(
    string OsVersion,
    string OsBuild,
    string? CpuName,
    int CpuCores,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<GpuInfo> Gpus,
    string? CudaVersion,
    DateTime CollectedAt)
{
    public string TotalMemoryDisplay => FormatBytes(TotalMemoryBytes);
    public string AvailableMemoryDisplay => FormatBytes(AvailableMemoryBytes);
    public string MemoryUsagePercent =>
        TotalMemoryBytes > 0
            ? $"{100.0 * (TotalMemoryBytes - AvailableMemoryBytes) / TotalMemoryBytes:F1}%"
            : "?";

    private static string FormatBytes(long bytes)
    {
        const double GB = 1024.0 * 1024.0 * 1024.0;
        const double MB = 1024.0 * 1024.0;
        if (bytes >= GB) return $"{bytes / GB:F1} GB ({bytes:N0} B)";
        if (bytes >= MB) return $"{bytes / MB:F0} MB ({bytes:N0} B)";
        return $"{bytes:N0} B";
    }
}

public sealed record DiskInfo(
    string DriveLetter,
    long TotalBytes,
    long AvailableBytes)
{
    public string TotalDisplay => Format(TotalBytes);
    public string AvailableDisplay => Format(AvailableBytes);
    public string UsagePercent =>
        TotalBytes > 0
            ? $"{100.0 * (TotalBytes - AvailableBytes) / TotalBytes:F1}%"
            : "?";

    private static string Format(long bytes)
    {
        const double GB = 1024.0 * 1024.0 * 1024.0;
        return bytes >= GB ? $"{bytes / GB:F1} GB" : $"{bytes / (1024.0 * 1024.0):F0} MB";
    }
}

public sealed record GpuInfo(
    int Index,
    string Name,
    string DriverVersion,
    long MemoryBytes)
{
    public string MemoryDisplay
    {
        get
        {
            const double GB = 1024.0 * 1024.0 * 1024.0;
            return MemoryBytes >= GB
                ? $"{MemoryBytes / GB:F1} GB"
                : $"{MemoryBytes / (1024.0 * 1024.0):F0} MB";
        }
    }
}