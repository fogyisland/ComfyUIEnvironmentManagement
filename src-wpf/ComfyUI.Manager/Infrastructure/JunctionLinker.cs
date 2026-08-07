using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// JunctionLinker:Windows 上创建目录 junction(符号链接到目录)。
///
/// 用 <c>cmd /c mklink /D &lt;link&gt; &lt;target&gt;</c> 实现。M5.2 替代了
/// Python <c>FS.create_junction</c>(T9 删)。macOS/Linux 上应该用 ln -s,
/// 但本项目目前只跑 Windows。
/// </summary>
public class JunctionLinker
{
    public sealed class JunctionCreationException : Exception
    {
        public JunctionCreationException(string message, int exitCode, string stderr)
            : base($"{message} (exit={exitCode}): {stderr}") { }
    }

    /// <summary>
    /// CreateAsync:在 <paramref name="linkPath"/> 创建指向 <paramref name="targetPath"/> 的 junction。
    /// </summary>
    public virtual async Task CreateAsync(
        string linkPath,
        string targetPath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(targetPath))
            throw new JunctionCreationException(
                $"junction target 不存在: {targetPath}", -1, "");

        if (Directory.Exists(linkPath) || File.Exists(linkPath))
            throw new JunctionCreationException(
                $"link 路径已存在: {linkPath}", -1, "");

        // 确保父目录存在
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /D \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new JunctionCreationException("cmd 启动失败", -1, "");

        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
            throw new JunctionCreationException(
                "mklink 失败", p.ExitCode, stderr);
    }

    /// <summary>
    /// GetTargetAsync:如果 <paramref name="linkPath"/> 是一个链接(junction / 目录符号链接),
    /// 返回它的 target 全路径;不是链接(普通目录)返回 null;不存在则抛
    /// <see cref="JunctionCreationException"/>。
    ///
    /// 实现说明:计划原稿写的是跑 <c>cmd /c dir /AL &lt;linkPath&gt;</c> 解析 <c>&lt;JUNCTION&gt;</c> 行,
    /// 实测该做法不可行(见 v0.6.7.3 T2 report):
    ///   1. <see cref="CreateAsync"/> 用的是 <c>mklink /D</c>,产物是 <c>&lt;SYMLINKD&gt;</c> 不是 <c>&lt;JUNCTION&gt;</c>;
    ///   2. <c>dir /AL</c> 作用在 link 自身时会*穿过*链接去列 target 的内容,列不到链接本身;
    ///   3. dir 的格式是 <c>&lt;SYMLINKD&gt; &lt;名字&gt; [&lt;target&gt;]</c>,target 在方括号里;
    ///   4. 没有链接的普通目录 <c>dir /AL</c> 退出码是 1,会误判成错误。
    /// 因此改用 BCL 的 <see cref="Directory.ResolveLinkTarget"/>(.NET 6+),
    /// 它对 junction 和 symlink 都有效,且不依赖 cmd 输出的本地化文案。
    /// </summary>
    public virtual Task<string?> GetTargetAsync(
        string linkPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(linkPath))
            throw new JunctionCreationException(
                $"link 路径不存在: {linkPath}", -1, "");

        // 不是链接(普通目录)时返回 null。
        // returnFinalTarget:false → 只解析一层,避免 target 已被删除时抛 IOException,
        // 这样"断掉的 junction"也能拿到它原本指向的路径(上层要靠这个判断是否需要重建)。
        var info = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: false);
        if (info is null)
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(Path.GetFullPath(info.FullName));
    }

    /// <summary>
    /// CopyDirectoryAsync:把 <paramref name="sourceDir"/> 递归复制到 <paramref name="destDir"/>。
    /// 用于 "independent" 布局(ComfyUI 独立,不共享)。
    /// </summary>
    public virtual void CopyDirectory(string sourceDir, string destDir)
    {
        var source = new DirectoryInfo(sourceDir);
        if (!source.Exists)
            throw new JunctionCreationException(
                $"copy source 不存在: {sourceDir}", -1, "");

        CopyRecursive(source, new DirectoryInfo(destDir));
    }

    private static void CopyRecursive(DirectoryInfo source, DirectoryInfo dest)
    {
        Directory.CreateDirectory(dest.FullName);
        foreach (var f in source.EnumerateFiles())
        {
            f.CopyTo(Path.Combine(dest.FullName, f.Name), overwrite: false);
        }
        foreach (var sub in source.EnumerateDirectories())
        {
            CopyRecursive(sub, new DirectoryInfo(Path.Combine(dest.FullName, sub.Name)));
        }
    }
}
