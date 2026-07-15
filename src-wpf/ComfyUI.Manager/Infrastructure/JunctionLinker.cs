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
public sealed class JunctionLinker
{
    public sealed class JunctionCreationException : Exception
    {
        public JunctionCreationException(string message, int exitCode, string stderr)
            : base($"{message} (exit={exitCode}): {stderr}") { }
    }

    /// <summary>
    /// CreateAsync:在 <paramref name="linkPath"/> 创建指向 <paramref name="targetPath"/> 的 junction。
    /// </summary>
    public async Task CreateAsync(
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
    /// CopyDirectoryAsync:把 <paramref name="sourceDir"/> 递归复制到 <paramref name="destDir"/>。
    /// 用于 "independent" 布局(ComfyUI 独立,不共享)。
    /// </summary>
    public void CopyDirectory(string sourceDir, string destDir)
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
