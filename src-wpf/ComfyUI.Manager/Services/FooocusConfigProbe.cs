using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T23b:Fooocus launcher 配置 probe ——
/// 通过 spawn env 内 venv Python(<c>&lt;env&gt;/venv/Scripts/python.exe</c>),
/// 跑 <c>python -c "import json; from modules import config; print(json.dumps({...}))"</c>
/// 拿 4 个下载 dict(checkpoint_downloads / lora_downloads / embeddings_downloads /
/// vae_downloads)+ 5 个 path(checkpoints / loras / embeddings / vae),返
/// <see cref="FooocusLauncherConfig"/>。镜像 Fooocus 自身 launcher 行为,
/// WPF 端预下 launch.py line 131-140 启动时自动下载的 5GB SDXL checkpoint,
/// 避免网络超时 crash env。
///
/// 设计:Python side 写一个一次性 -c script dump JSON;WPF side capture stdout
/// 解析。镜像 <see cref="EnvComponentReportBuilder.RunCommandAsync"/>
/// (Services/EnvComponentReportBuilder.cs:132-197) 的 subprocess 模式。
///
/// Python 3.10+ 才能用 <c>from modules import config</c>(env 启动用 venv 内 python,
/// 跟 Fooocus launcher 同版本)。
/// </summary>
public static class FooocusConfigProbe
{
    /// <summary>
    /// Spawn env venv Python 跑 probe script,capture stdout,parse JSON 返
    /// <see cref="FooocusLauncherConfig"/>。失败(超时 / Python 错 / JSON 错 / 模块
    /// import 失败)→ 返 null + 写 logProgress 错误。
    /// </summary>
    public static async Task<FooocusLauncherConfig?> ProbeAsync(
        Environment env,
        IProgress<string>? logProgress = null,
        CancellationToken ct = default)
    {
        if (env is null || string.IsNullOrWhiteSpace(env.RootPath))
            throw new ArgumentException("env / env.RootPath 为空", nameof(env));

        var venvPython = Path.Combine(env.RootPath, "venv", "Scripts", "python.exe");
        if (!File.Exists(venvPython))
        {
            logProgress?.Report($"[fooocus-probe] ✗ venv python 不存在:{venvPython}(env 没装 venv?)");
            return null;
        }

        // 镜像 EnvComponentReportBuilder.RunCommandAsync line 132-197:
        // 工作目录 = env.RootPath(让 from modules import config 解析到 env 内 modules)
        // PYTHONIOENCODING=utf-8 镜像 T23a — 确保 stdout 是 UTF-8(避免 mojibake)
        // PYTHONUTF8=1 镜像 T21 — 文件 I/O UTF-8 mode
        // 30s timeout(快速失败,probe 不应该 hang)
        var psi = new ProcessStartInfo
        {
            FileName = venvPython,
            WorkingDirectory = env.RootPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        PipProcessHelpers.ApplyUtf8Mode(psi);  // PYTHONUTF8=1
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        // Python -c script — 用 json.dumps 序列化 4 dict + 5 path,标准输出 1 行 JSON。
        // 注意:paths_checkpoints / paths_loras 是 list(Fooocus 命名 quirk),
        // path_embeddings / path_vae 是 singular string —— 我们用 json.dumps
        // 让 Python 自动处理 list vs str 差异。
        const string PythonScript = """
            import json, sys
            try:
                from modules import config
            except Exception as e:
                print("PROBE_ERROR:" + repr(e)); sys.exit(1)
            try:
                payload = {
                    "checkpoint_downloads": dict(getattr(config, "checkpoint_downloads", {}) or {}),
                    "lora_downloads": dict(getattr(config, "lora_downloads", {}) or {}),
                    "embeddings_downloads": dict(getattr(config, "embeddings_downloads", {}) or {}),
                    "vae_downloads": dict(getattr(config, "vae_downloads", {}) or {}),
                    "paths": {
                        "checkpoints": (getattr(config, "paths_checkpoints", None) or [getattr(config, "path_checkpoints", None) or "models/checkpoints"])[0] if (getattr(config, "paths_checkpoints", None) or [getattr(config, "path_checkpoints", None)]) else "models/checkpoints",
                        "loras": (getattr(config, "paths_loras", None) or [getattr(config, "path_loras", None) or "models/loras"])[0] if (getattr(config, "paths_loras", None) or [getattr(config, "path_loras", None)]) else "models/loras",
                        "embeddings": getattr(config, "path_embeddings", None) or "models/embeddings",
                        "vae": getattr(config, "path_vae", None) or "models/vae",
                    },
                }
                print("PROBE_OK:" + json.dumps(payload, ensure_ascii=False))
            except Exception as e:
                print("PROBE_ERROR:" + repr(e)); sys.exit(1)
            """;

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(PythonScript);

        logProgress?.Report($"[fooocus-probe] ↓ 启动 venv python probe (timeout 30s)...");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                logProgress?.Report("[fooocus-probe] ✗ Process.Start 返 null");
                return null;
            }

            // 30s timeout(整体 probe 不应超过这个时间)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logProgress?.Report($"[fooocus-probe] ✗ Python 退出码 {process.ExitCode}: {stderr.Trim()}");
                return null;
            }

            return ParseStdout(stdout, logProgress);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logProgress?.Report("[fooocus-probe] ✗ 30s 超时(venv python 启动 hang 或 from modules 卡)");
            return null;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"[fooocus-probe] ✗ 异常:{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 probe stdout —— 期望格式 <c>PROBE_OK:{json}</c> 或 <c>PROBE_ERROR:{repr}</c>。
    /// 单独抽出来便于 unit test(不需要真起 Python subprocess)。
    /// </summary>
    public static FooocusLauncherConfig? ParseStdout(
        string stdout,
        IProgress<string>? logProgress = null)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            logProgress?.Report("[fooocus-probe] ✗ stdout 空(可能 Python 启动失败)");
            return null;
        }

        var trimmed = stdout.Trim();
        const string OkPrefix = "PROBE_OK:";
        const string ErrPrefix = "PROBE_ERROR:";

        if (trimmed.StartsWith(ErrPrefix))
        {
            logProgress?.Report($"[fooocus-probe] ✗ Python 端错误:{trimmed.Substring(ErrPrefix.Length)}");
            return null;
        }
        if (!trimmed.StartsWith(OkPrefix))
        {
            logProgress?.Report($"[fooocus-probe] ✗ stdout 缺 PROBE_OK 前缀:{trimmed[..Math.Min(80, trimmed.Length)]}");
            return null;
        }

        var json = trimmed.Substring(OkPrefix.Length);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new FooocusLauncherConfig(
                CheckpointDownloads: ReadDict(root, "checkpoint_downloads"),
                LoraDownloads: ReadDict(root, "lora_downloads"),
                EmbeddingsDownloads: ReadDict(root, "embeddings_downloads"),
                VaeDownloads: ReadDict(root, "vae_downloads"),
                Paths: ReadDict(root.GetProperty("paths"), null));
        }
        catch (JsonException ex)
        {
            logProgress?.Report($"[fooocus-probe] ✗ JSON 解析失败:{ex.Message}");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> ReadDict(JsonElement root, string? propName)
    {
        var element = propName is null ? root : root.GetProperty(propName);
        var dict = new Dictionary<string, string>();
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString() ?? "";
        }
        return dict;
    }
}
