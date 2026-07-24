using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Data;

public sealed class PyTorchVersionCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    public const string FileName = "pytorch_versions_cache.json";

    public PyTorchVersionCache(string appDataDir)
    {
        FilePath = Path.Combine(appDataDir, FileName);
    }

    public string FilePath { get; }

    public async Task<PyTorchLiveVersions?> TryReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct);
            var versions = JsonSerializer.Deserialize<PyTorchLiveVersions>(json);
            if (versions is null || versions.FetchedAt + Ttl < DateTimeOffset.UtcNow)
            {
                return null;
            }

            return versions;
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteAsync(PyTorchLiveVersions versions, CancellationToken ct = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(versions);
            await File.WriteAllTextAsync(FilePath, json, ct);
        }
        catch
        {
        }
    }
}
