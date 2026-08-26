using System;
using System.IO;
using System.Text.Json;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x:读写 <see cref="EnvMarker"/> 隐藏文件(<c>.cmgr-env.json</c>)。
///
/// 写时:
///   - JSON 序列化(<see cref="JsonOptions.Default"/> — camelCase + null 忽略,
///     跟 settings.inf 一致)
///   - FileAttributes.Hidden(Windows 显示隐藏文件时才会看到)
///   - 原子写:先写 <c>.cmgr-env.json.tmp</c> → File.Move(tmp, final, overwrite: true)
///     避免中途 crash 留下半截 marker
///
/// 读时:
///   - 不存在 → null(scanner 跳过这个子目录)
///   - JSON 解析失败 / schema_version 不支持 → null(scanner 跳过)
///   - 必要字段(envId/name/kind)缺失 → null
///   - 任何 IO 异常 → null
///
/// 读失败一律 null 而非抛,scanner 不能因为一个坏 marker 阻塞整轮扫描。
/// </summary>
public static class EnvMarkerService
{
    /// <summary>
    /// 写 <paramref name="marker"/> 到 <paramref name="envDir"/>/<see cref="EnvMarker.FileName"/>。
    /// FileAttributes.Hidden(只在 Windows 显示 "隐藏文件" 时可见)。
    /// 原子写:tmp + rename,crash-safe。
    /// </summary>
    /// <returns>
    /// true = 写盘成功;false = IO 失败(磁盘满 / 权限 / 路径无效)。
    /// 调用方一般忽略 false(env-create 主流程不因 marker 失败而失败,G5)。
    /// </returns>
    public static bool Write(string envDir, EnvMarker marker)
    {
        if (string.IsNullOrWhiteSpace(envDir) || marker is null) return false;
        try
        {
            Directory.CreateDirectory(envDir);
            var finalPath = Path.Combine(envDir, EnvMarker.FileName);
            var tmpPath = finalPath + ".tmp";
            var json = JsonSerializer.Serialize(marker, JsonOptions.Default);
            File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);

            // File.Move overwrite 是 .NET 5+ 才有,这里直接 Delete + Move 兼容 .NET 8。
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);

            // FileAttributes.Hidden — Windows Explorer 默认不显示。
            // 不读 Attribute 再 OR — 旧 marker 可能有 ReadOnly 标志,保留。
            var attrs = File.GetAttributes(finalPath);
            if ((attrs & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(finalPath, attrs | FileAttributes.Hidden);
            }
            return true;
        }
        catch
        {
            // IO 失败 → 静默返回 false;调用方决定是否容忍。
            return false;
        }
    }

    /// <summary>
    /// 读 <paramref name="envDir"/>/<see cref="EnvMarker.FileName"/>。
    /// 失败(missing / malformed / wrong schema)返回 null,**不抛**。
    /// </summary>
    public static EnvMarker? Read(string envDir)
    {
        if (string.IsNullOrWhiteSpace(envDir)) return null;
        var path = Path.Combine(envDir, EnvMarker.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var marker = JsonSerializer.Deserialize<EnvMarker>(json, JsonOptions.Default);
            if (marker is null) return null;

            // schema 校验
            if (marker.SchemaVersion != EnvMarker.CurrentSchemaVersion) return null;
            if (string.IsNullOrWhiteSpace(marker.EnvId)) return null;
            if (string.IsNullOrWhiteSpace(marker.Kind)) return null;
            return marker;
        }
        catch
        {
            return null;
        }
    }
}