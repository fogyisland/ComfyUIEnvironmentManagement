using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-08-30):env-create step 6.7 — 下载 + 解压 Astral uv 到
/// <c>&lt;env&gt;/tools/uv/uv.exe</c>。
///
/// uv 是 Lightricks/LTX-2 monorepo 安装前置(<c>uv sync --extra natten</c>),
/// 项目从未用过 uv。装到 env 内部(不进 PATH)→ 用户机器 / 项目搬家都能用。
/// 下载源 <c>https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip</c>。
///
/// 测试可注入 downloader / versionProber;默认实现走 HttpClient。
/// </summary>
public sealed class UvInstaller
{
    public const string DownloadUrl =
        "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

    private readonly string _envRoot;
    private readonly Func<Uri, string?, CancellationToken, Task<byte[]>> _downloader;
    private readonly Func<string, CancellationToken, Task<(byte[] Stdout, int ExitCode)>> _versionProber;
    private readonly string _uvExePath;

    public UvInstaller(
        string envRoot,
        Func<Uri, string?, CancellationToken, Task<byte[]>>? downloader = null,
        Func<string, CancellationToken, Task<(byte[] Stdout, int ExitCode)>>? versionProber = null,
        HttpClient? httpClient = null)
    {
        _envRoot = envRoot ?? throw new ArgumentNullException(nameof(envRoot));
        var uvDir = Path.Combine(_envRoot, "tools", "uv");
        _uvExePath = Path.Combine(uvDir, "uv.exe");

        if (downloader is not null)
        {
            _downloader = downloader;
        }
        else
        {
            var client = httpClient ?? new HttpClient();
            _downloader = async (uri, _, ct) =>
            {
                using var resp = await client.GetAsync(uri, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            };
        }

        if (versionProber is not null)
        {
            _versionProber = versionProber;
        }
        else
        {
            _versionProber = async (exe, ct) =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                var combined = string.IsNullOrEmpty(stdout) ? stderr : stdout;
                return (System.Text.Encoding.UTF8.GetBytes(combined), p.ExitCode);
            };
        }
    }

    /// <summary>
    /// 下载 + 解压 uv → <c>env/tools/uv/uv.exe</c>,然后跑 <c>--version</c> 校验。
    /// 已存在 + 文件非 0 字节 + 校验成功 → 直接返路径(不重下)。
    /// </summary>
    /// <returns>uv.exe 绝对路径。</returns>
    /// <exception cref="InvalidOperationException">下载 / 解压 / 校验失败时 throw。</exception>
    public async Task<string> InstallAsync(CancellationToken ct = default)
    {
        if (IsAlreadyInstalled())
        {
            return _uvExePath;
        }

        // 重下前清理残缺文件
        try { File.Delete(_uvExePath); } catch { }
        var uvDir = Path.GetDirectoryName(_uvExePath)!;
        Directory.CreateDirectory(uvDir);

        var bytes = await _downloader(new Uri(DownloadUrl), null, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException($"uv 下载内容为空({DownloadUrl})");

        using var zipStream = new MemoryStream(bytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("uv.exe");
        if (entry is null)
            throw new InvalidOperationException($"uv.zip 内找不到 uv.exe entry");

        entry.ExtractToFile(_uvExePath, overwrite: true);

        var (stdout, exit) = await _versionProber(_uvExePath, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            try { File.Delete(_uvExePath); } catch { }
            throw new InvalidOperationException(
                $"uv --version 校验失败(exit={exit}): {System.Text.Encoding.UTF8.GetString(stdout)}");
        }
        return _uvExePath;
    }

    private bool IsAlreadyInstalled()
    {
        return File.Exists(_uvExePath) && new FileInfo(_uvExePath).Length > 0;
    }
}
