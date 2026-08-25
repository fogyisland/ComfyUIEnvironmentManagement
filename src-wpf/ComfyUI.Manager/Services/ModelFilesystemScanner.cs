using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Services;

/// <summary>v1.0.0 T13:Optional context for hash computation + bulk match during scan.
/// All fields nullable for back-compat. Pass null <see cref="ScanContext"/> (or omit)
/// for the legacy pure-enumeration scan used by 23 existing tests.</summary>
public sealed class ScanContext
{
    public CivitaiHashCache? HashCache { get; init; }
    public CivitaiMatcherOrchestrator? Matcher { get; init; }
    public IProgress<string>? Progress { get; init; }
}

/// <summary>v0.6.20:扫描 ModelsDirectory 找到已下载的 model versions。
/// v1.0.0 T5:同遍既识别 meta.json 三层布局 <see cref="SourceKind"/> meta.json paths,
/// 也识别标准 ComfyUI 二层布局 <kind>/<model>/<file>.ext (Source="Local", SourceId="local:...").
/// v1.0.0 T6:同遍也识别扁平布局 <kind>/<file>.ext,每顶层 model 文件 = 1 条记录(SourceId="local:{kind}/{file}".ToLowerInvariant())。
/// v1.0.0 T7:3-level 二层布局也改成 per-file(每 <kind>/<model>/<file>.ext = 1 card,Title=文件名),统一 flat/3-level 都走 BuildFlatModel helper。
/// v1.0.0 T13:可选 <see cref="ScanContext"/> 启用 hash compute + bulk match + cover download。</summary>
public class ModelFilesystemScanner
{
    private static readonly Dictionary<string, ModelKind> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["checkpoint"]      = ModelKind.Checkpoint,
        ["checkpoints"]     = ModelKind.Checkpoint,
        ["lora"]            = ModelKind.LORA,
        ["loras"]           = ModelKind.LORA,
        ["vae"]             = ModelKind.VAE,
        ["controlnet"]      = ModelKind.Controlnet,
        ["embedding"]       = ModelKind.TextualInversion,
        ["embeddings"]      = ModelKind.TextualInversion,
        ["textualinversion"]= ModelKind.TextualInversion,
        ["upscale"]         = ModelKind.Upscaler,
        ["upscaler"]        = ModelKind.Upscaler,
        ["upscale_models"]  = ModelKind.Upscaler,
        ["hypernetwork"]    = ModelKind.Hypernetwork,
        ["hypernetworks"]   = ModelKind.Hypernetwork,
        // v1.0.0 T12:Diffusers 文件夹模型 — kindDir 名 = "diffusers" 时也走 Diffusers 检测
        // (注意:即便 kindDir 不是 diffusers,subdir 有 model_index.json 仍 emit Diffusers — Kind 强写)
        ["diffusers"]       = ModelKind.Diffusers,
        ["diffuser"]        = ModelKind.Diffusers,
    };

    private static readonly HashSet<string> ModelFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".ckpt", ".pt", ".pth", ".bin", ".onnx", ".gguf",
    };

    /// <summary>v1.0.0 T10:preview image 同目录 sibling 扫描 — 同 basename + image extension set,
    /// 字典序 first match。WPF Image 原生支持 .gif 动画。</summary>
    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
    };

    private readonly AppLogger? _logger;

    public ModelFilesystemScanner(AppLogger? logger = null)
    {
        _logger = logger;
    }

    public virtual IReadOnlyList<DownloadedModel> Scan(string modelsDir) => Scan(modelsDir, ctx: null);

    /// <summary>v1.0.0 T13:Scan with optional hash compute + bulk match + cover download.
    /// When <paramref name="ctx"/> is null, behaves exactly like the legacy
    /// <see cref="Scan(string)"/> overload.</summary>
    public virtual IReadOnlyList<DownloadedModel> Scan(string modelsDir, ScanContext? ctx)
    {
        var raw = ScanCore(modelsDir);
        if (ctx is null) return raw;
        return HashAndMatch(raw, ctx);
    }

    private IReadOnlyList<DownloadedModel> ScanCore(string modelsDir)
    {
        var results = new List<DownloadedModel>();
        if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
            return results;

        // 三布局同遍:
        //   meta.json 三层: <kind>/<model>/<version>/meta.json     (v0.6.20 marketplace)
        //   标准二层 (T7): <kind>/<model>/<file>.{ext}             (每文件 = 1 card, Title=文件名)
        //   扁平布局 (T6): <kind>/<file>.{ext}                      (每顶层 model 文件 = 1 record)
        foreach (var kindDir in Directory.EnumerateDirectories(modelsDir))
        {
            var kindName = Path.GetFileName(kindDir);

            foreach (var modelDir in Directory.EnumerateDirectories(kindDir))
            {
                // v1.0.0 T12:Diffusers 文件夹模型 — 同 subdir 内存在 model_index.json 即视为 1 个 Diffusers 模型卡,
                // 不再递归 per-file 扫 unet/ 等子目录里的 .safetensors(那些是模型文件组件,不是独立模型)。
                // Kind 强写为 Diffusers(不依赖 kindDir 名推断 — 即便 kindDir="checkpoints",
                // 内部 subdir 有 model_index.json 仍认 Diffusers,semantic 比 dir name 更准确)。
                if (File.Exists(Path.Combine(modelDir, "model_index.json")))
                {
                    var subdirName = Path.GetFileName(modelDir);
                    // 跳过 hidden dirs(.DS_Store, .git 等)
                    if (!subdirName.StartsWith("."))
                    {
                        var title = ResolveDiffusersTitle(modelDir, subdirName, _logger);
                        DateTime latestMtime;
                        try
                        {
                            latestMtime = Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories)
                                .Select(File.GetLastWriteTime)
                                .DefaultIfEmpty(DateTime.MinValue)
                                .Max();
                        }
                        catch (Exception ex)
                        {
                            // v1.0.0 T-D3:symlink loops or perms errors — skip folder, don't crash scan
                            _logger?.Warn("model-scanner",
                                $"skip {modelDir}: enumerate failed {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }
                        var previewPath = FindFirstPngInDir(modelDir);
                        results.Add(new DownloadedModel
                        {
                            Title = title,
                            SubfolderName = kindName,
                            FullPath = modelDir,                                      // 目录路径,不是文件路径
                            Kind = ModelKind.Diffusers,                               // 强类型 = Diffusers
                            Source = "Local",
                            SourceId = $"local:{kindName}/{subdirName}".ToLowerInvariant(),
                            SourceVersionId = "",
                            DownloadedAt = latestMtime,                               // 子目录内最新文件 mtime(递归)
                            PreviewImagePath = previewPath,                           // subdir 内字典序 first .png
                        });
                    }
                    continue;   // 跳过后续 meta.json 路径 + 3-level per-file 扫描
                }

                // 现有 meta.json 路径: <kind>/<model>/<version>/meta.json
                foreach (var versionDir in Directory.EnumerateDirectories(modelDir))
                {
                    var metaPath = Path.Combine(versionDir, "meta.json");
                    if (!File.Exists(metaPath))
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: missing meta.json");
                        continue;
                    }

                    try
                    {
                        var json = File.ReadAllText(metaPath);
                        var sidecar = JsonSerializer.Deserialize<ModelMetaSidecar>(json,
                            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
                        if (sidecar is null)
                        {
                            _logger?.Warn("model-scanner", $"skip {versionDir}: meta.json null");
                            continue;
                        }

                        results.Add(new DownloadedModel
                        {
                            SubfolderName = Path.GetFileName(versionDir),
                            FullPath = versionDir,
                            Kind = sidecar.Kind,
                            Title = sidecar.Title,
                            Source = sidecar.Source,
                            SourceId = sidecar.SourceId,
                            SourceVersionId = sidecar.SourceVersionId,
                            DownloadedAt = sidecar.DownloadedAt,
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn("model-scanner", $"skip {versionDir}: parse fail {ex.Message}");
                    }
                }

                // 3-level standard layout (T5 → T7): <kind>/<model>/<file>.ext — 每个文件 = 1 卡
                foreach (var file in Directory.EnumerateFiles(modelDir))
                {
                    if (!ModelFileExtensions.Contains(Path.GetExtension(file)))
                        continue;
                    var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                    results.Add(BuildFlatModel(kindName, fileNameNoExt, file));
                }
            }

            // 扁平布局 (T6): <kind>/<file>.ext 直接子文件 — 每个文件 = 1 卡
            //   SourceId 与 3-level 路径格式相同("local:{kind}/{filename-no-ext}") — 同文件无论布局只 1 卡
            foreach (var file in Directory.EnumerateFiles(kindDir))
            {
                if (!ModelFileExtensions.Contains(Path.GetExtension(file)))
                    continue;
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                results.Add(BuildFlatModel(kindName, fileNameNoExt, file));
            }
        }

        return results;
    }

    private static DownloadedModel BuildFlatModel(string kindName, string fileNameNoExt, string fullPath)
    {
        return new DownloadedModel
        {
            SubfolderName = kindName,                                // 统一 flat/3-level 都用 kindName
            FullPath = fullPath,
            Title = PrettyPrint(fileNameNoExt),                      // 用文件名(不是 dir 名)
            Source = "Local",
            SourceId = $"local:{kindName}/{fileNameNoExt}".ToLowerInvariant(),  // 统一 flat/3-level 格式
            SourceVersionId = "",
            DownloadedAt = File.GetLastWriteTime(fullPath),          // 文件 mtime(不是 dir mtime)
            Kind = InferKind(kindName),
            PreviewImagePath = FindPreviewImage(fullPath),           // v1.0.0 T10:同目录 sibling scan
        };
    }

    /// <summary>v1.0.0 T10:同目录扫同 basename + image extension set,字典序 first match。
    /// 不递归,不交叉 kind 子目录。无 image → null。
    /// meta.json path 不调此 helper(直接 new DownloadedModel,PreviewImagePath 留默认 null)。</summary>
    private static string? FindPreviewImage(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var basenameNoExt = Path.GetFileNameWithoutExtension(fullPath);
        var candidates = Directory.EnumerateFiles(dir, $"{basenameNoExt}.*")
            .Where(f => PreviewImageExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal);   // 字典序 first
        return candidates.FirstOrDefault();
    }

    /// <summary>v1.0.0 T12:Diffusers 文件夹模型 — subdir 内第一个 .png (字典序,顶层, 不递归)。
    /// 跟 FindPreviewImage 不同:这个找 subdir 内**任意** .png(无 basename 约束,Diffusers folder 里 preview 图通常不跟 dir 同名),
    /// 不递归(unet/preview.png 这种子目录里的图忽略,只看 Diffusers 根目录的 preview 图)。
    /// 无 .png → null(卡片显示 kind badge fallback)。</summary>
    private static string? FindFirstPngInDir(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return null;
        return Directory.EnumerateFiles(dirPath, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>v1.0.0 T-D1:Select a single file to hash from a Diffusers folder.
    /// Priority order (first match wins):
    /// 1. <c>unet/diffusion_pytorch_model.safetensors</c> (SD 1.5 / SDXL canonical)
    /// 2. <c>transformer/diffusion_pytorch_model.safetensors</c> (FLUX-style)
    /// 3. <c>unet/diffusion_pytorch_model.bin</c> (legacy .bin variant)
    /// 4-7. Largest file in folder (recursive) by extension preference:
    ///      <c>.safetensors</c> → <c>.bin</c> → <c>.ckpt</c> → <c>.pt</c>
    /// 8. None → return <c>null</c> (orchestrator may still match via safetensors/companion/filename).
    /// Internal so tests can call directly via <c>InternalsVisibleTo</c>.</summary>
    internal static string? FindCanonicalHashFile(string dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) || !Directory.Exists(dirPath)) return null;

        foreach (var rel in new[]
        {
            "unet/diffusion_pytorch_model.safetensors",
            "transformer/diffusion_pytorch_model.safetensors",
            "unet/diffusion_pytorch_model.bin",
        })
        {
            // Use '/' separator inside rel, then normalize for Windows so returned path
            // matches the test fixture's Path.Combine(...) output (all-platform separator).
            var p = Path.GetFullPath(Path.Combine(dirPath, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(p)) return p;
        }

        foreach (var ext in new[] { ".safetensors", ".bin", ".ckpt", ".pt" })
        {
            string? largest = null;
            long maxLen = -1;
            foreach (var f in Directory.EnumerateFiles(dirPath, "*" + ext, SearchOption.AllDirectories))
            {
                var len = new FileInfo(f).Length;
                if (len > maxLen) { maxLen = len; largest = f; }
            }
            if (largest is not null) return largest;
        }

        return null;
    }

    /// <summary>v1.0.0 T-D3:Extract Title from <c>model_index.json["name"]</c>.
    /// Falls back to <paramref name="fallbackName"/> (folder name) when:
    /// file is empty, JSON is invalid, or <c>name</c> field is missing/empty/non-string.
    /// Logs warning at <c>"model-scanner"</c> on invalid JSON; silent on missing field.</summary>
    private static string ResolveDiffusersTitle(string modelDir, string fallbackName, AppLogger? logger = null)
    {
        var indexPath = Path.Combine(modelDir, "model_index.json");
        try
        {
            var json = File.ReadAllText(indexPath);
            if (string.IsNullOrWhiteSpace(json)) return fallbackName;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("name", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(nameEl.GetString()))
            {
                return nameEl.GetString()!;
            }
        }
        catch (Exception ex)
        {
            // Invalid JSON or IO error — fall back, log so user can investigate
            logger?.Warn("model-scanner",
                $"invalid model_index.json at {modelDir}: {ex.GetType().Name}: {ex.Message}");
        }
        return fallbackName;
    }

    private static ModelKind InferKind(string kindDirName)
        => KindAliases.TryGetValue(kindDirName, out var k) ? k : ModelKind.Other;

    private static string PrettyPrint(string raw)
    {
        var spaced = raw.Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    /// <summary>v1.0.0 T13:Compute SHA256 for each model (parallel, max 4 concurrent, with cache),
    /// then run the matcher chain sequentially per model and download cover images.
    /// <see cref="DownloadedModel"/> is a class with init-only properties, so mutations
    /// produce a new instance with augmented <c>Hash</c>/<c>MatchedDetail</c>/<c>MatchSource</c> fields.</summary>
    private IReadOnlyList<DownloadedModel> HashAndMatch(IReadOnlyList<DownloadedModel> raw, ScanContext ctx)
    {
        var n = raw.Count;
        var byIndex = new DownloadedModel?[n];
        for (int k = 0; k < n; k++) byIndex[k] = raw[k];

        // Parallel hash compute (max 4 concurrent)
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 4 }, k =>
        {
            try
            {
                var m = byIndex[k]!;
                if (m.Hash is not null || ctx.HashCache is null) return;
                if (string.IsNullOrEmpty(m.FullPath)) return;

                // v1.0.0 T-D2:resolve hash target — file for single-file models, canonical file inside
                // Diffusers folder for multi-file. Skip if neither exists.
                string? hashTarget;
                long sizeBytes;
                long mtimeTicks;
                if (File.Exists(m.FullPath))
                {
                    hashTarget = m.FullPath;
                    var info = new FileInfo(m.FullPath);
                    sizeBytes = info.Length;
                    mtimeTicks = info.LastWriteTimeUtc.Ticks;
                }
                else if (Directory.Exists(m.FullPath))
                {
                    hashTarget = FindCanonicalHashFile(m.FullPath);
                    if (hashTarget is null) return;   // no hashable file — orchestrator may still match other strategies
                    var files = Directory.EnumerateFiles(m.FullPath, "*", SearchOption.AllDirectories).ToList();
                    sizeBytes = files.Sum(f => new FileInfo(f).Length);
                    mtimeTicks = files.Count > 0
                        ? files.Max(f => new FileInfo(f).LastWriteTimeUtc.Ticks)
                        : 0;
                }
                else
                {
                    return;
                }

                // Cache key uses the folder path for Diffusers (so it survives adding/removing files).
                // For single-file models, m.FullPath == hashTarget so the cache key is unchanged.
                var cached = ctx.HashCache.Lookup(m.FullPath, sizeBytes, mtimeTicks);
                string hash;
                if (cached is not null)
                {
                    hash = cached;
                    ctx.Progress?.Report($"[hash] cache hit: {Path.GetFileName(hashTarget)}");
                }
                else
                {
                    hash = ModelHasher.ComputeSha256(hashTarget);
                    ctx.HashCache.Store(m.FullPath, sizeBytes, mtimeTicks, hash);
                    ctx.Progress?.Report($"[hash] computed: {Path.GetFileName(hashTarget)} → {hash[..8]}…");
                }
                byIndex[k] = CopyWith(m, hash: hash);
            }
            catch (Exception ex)
            {
                ctx.Progress?.Report($"[scan] ⚠ hash failed: {byIndex[k]!.FullPath} {ex.GetType().Name}: {ex.Message}");
            }
        });

        // Sequential match per model + cover download (no batch /by-hash endpoint yet — YAGNI for v1.0.0)
        for (int k = 0; k < n; k++)
        {
            var m = byIndex[k]!;
            if (m.Hash is null || ctx.Matcher is null) continue;
            MatchResult? result = null;
            try
            {
                result = ctx.Matcher.MatchAsync(m, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch { /* orchestrator logs and returns null on errors */ }
            if (result is null) continue;

            TryDownloadCover(m, result, ctx);
            byIndex[k] = CopyWith(m, matchedDetail: result.Detail, matchSource: result.Source);
            ctx.Progress?.Report($"[match] {k + 1}/{n} {m.Title} → {result.Source}");
        }

        var result2 = new List<DownloadedModel>(n);
        for (int k = 0; k < n; k++) result2.Add(byIndex[k]!);
        return result2;
    }

    /// <summary>Helper to construct a new <see cref="DownloadedModel"/> carrying the augmented field(s).
    /// DownloadedModel is a class with init-only properties — no <c>with</c> expression available.</summary>
    private static DownloadedModel CopyWith(
        DownloadedModel src,
        string? hash = null,
        CivitAiDetailDto? matchedDetail = null,
        MatchSource? matchSource = null) => new()
    {
        SubfolderName = src.SubfolderName,
        FullPath = src.FullPath,
        Kind = src.Kind,
        Title = src.Title,
        Source = src.Source,
        SourceId = src.SourceId,
        SourceVersionId = src.SourceVersionId,
        DownloadedAt = src.DownloadedAt,
        PreviewImagePath = src.PreviewImagePath,
        Hash = hash ?? src.Hash,
        MatchedDetail = matchedDetail ?? src.MatchedDetail,
        MatchSource = matchSource ?? src.MatchSource,
    };

    /// <summary>v1.0.0 T13:Idempotent cover image download to <c>&lt;basename&gt;.preview.png</c> next to the model.
    /// Skips if file already exists. Errors reported via <see cref="ScanContext.Progress"/> — never throws.</summary>
    private static void TryDownloadCover(DownloadedModel model, MatchResult result, ScanContext ctx)
    {
        if (string.IsNullOrEmpty(result.CoverImageUrl)) return;
        if (string.IsNullOrEmpty(model.FullPath)) return;
        var dir = Path.GetDirectoryName(model.FullPath);
        var basename = Path.GetFileNameWithoutExtension(model.FullPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename)) return;
        var target = Path.Combine(dir, $"{basename}.preview.png");
        if (File.Exists(target)) return;
        try
        {
            using var http = new HttpClient();
            var bytes = http.GetByteArrayAsync(result.CoverImageUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(target, bytes);
            ctx.Progress?.Report($"[preview] saved: {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            ctx.Progress?.Report($"[preview] ✗ download failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}