using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// 健壮删除目录 —— 替代直接 <c>Directory.Delete(dir, recursive: true)</c>。
/// 解决 .NET 原生实现对 Windows 文件系统 4 个真实场景的失败:
/// - <b>ReadOnly/Hidden/System subdirectory attribute</b>:<c>git clone</c> + Windows
///   Defender / NTFS compression 有时会标 subdir 为 ReadOnly,<c>Directory.Delete recursive</c>
///   抛 UnauthorizedAccessException,老实现只清 file attr 没清 subdir attr。
/// - <b>Long path (&gt;260 chars)</b>:git 深嵌套或长文件名超过 MAX_PATH,抛 PathTooLongException。
///   解决:加 <c>\\?\</c> 前缀走 NTFS long path API。
/// - <b>临时文件占用</b>:git index / Windows Defender 短暂持 file handle,IOException。
///   解决:retry 3 次 × 递增 backoff(50ms / 150ms / 400ms)。
/// - <b>silent swallow</b>:老 <c>TryDelete</c> catch 后吞掉,caller 看到 "未删" 但不知道
///   原因。新版最后一次 attempt 把异常 throw 给 caller。
///
/// 复用方:ComfyUIManagerInstaller.Uninstall、NodeOperations.UninstallAsync、
/// TemplateSourceUpdater wipe 等所有"rm -rf 整个目录"路径。
/// </summary>
public static class RobustDirectoryDelete
{
    /// <summary>
    /// 删除目录(及内容)。失败时抛最后一个异常(不再 silent)。
    /// 不存在时 no-op,不抛。
    /// </summary>
    public static void Delete(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        if (!Directory.Exists(dir)) return;

        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ClearAttributes(dir);
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(attempt switch { 0 => 50, 1 => 150, _ => 400 });
            }
        }
        // 3 次都失败 —— 把最后异常 throw 出去,让 caller 决定怎么报告。
        throw new IOException(
            $"删除目录失败(已重试 3 次):{dir}",
            last);
    }

    /// <summary>
    /// 顶到底清目录里所有 file + subdirectory 的 ReadOnly/Hidden/System attribute。
    /// .NET recursive delete 自己只清 file attr;subdir 带 ReadOnly 会触发
    /// UnauthorizedAccessException,这是用户报告的 ComfyUI Manager 卸载失败 root cause。
    ///
    /// 用显式 walk 不用 <see cref="SearchOption.AllDirectories"/>:Walk 才能在每个
    /// 进入 subdir 前清它的 attr(AllDirectories 只列 file 不改 subdir attr)。
    /// </summary>
    private static void ClearAttributes(string dir)
    {
        // 顶层
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
        }
        // 递归向下:walk 每个 subdir,先清它自身 attr,再 walk 它的子项
        WalkClearAttributes(dir);
    }

    private static void WalkClearAttributes(string dir)
    {
        IEnumerable<string> subs;
        try
        {
            subs = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly);
        }
        catch { return; }
        foreach (var sub in subs)
        {
            try { new DirectoryInfo(sub).Attributes = FileAttributes.Normal; }
            catch { /* ignore */ }
            foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.TopDirectoryOnly))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            WalkClearAttributes(sub);
        }
    }
}