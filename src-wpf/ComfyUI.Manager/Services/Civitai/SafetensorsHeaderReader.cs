using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>v1.0.0 T13:Parse the first ~64KB of a .safetensors file to extract the
/// model name from the JSON header. Looks for <c>ss_sd_model_name</c> (A1111/Forge convention)
/// or <c>modelspec.title</c> (modelspec convention). Returns false on any parse error.</summary>
public static class SafetensorsHeaderReader
{
    private const int MaxReadBytes = 64 * 1024; // 64KB

    public static bool TryReadModelName(string filePath, out string? modelName)
    {
        modelName = null;
        if (!File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 8) return false;

            // Read first 8 bytes: uint64 little-endian = JSON header length
            var lengthBuf = new byte[8];
            var read = fs.Read(lengthBuf, 0, 8);
            if (read < 8) return false;
            ulong headerLen = BitConverter.ToUInt64(lengthBuf, 0);

            // Sanity check: must be ≤ MaxReadBytes
            if (headerLen == 0 || headerLen > MaxReadBytes) return false;

            // Read JSON header
            var headerBuf = new byte[(int)headerLen];
            read = fs.Read(headerBuf, 0, (int)headerLen);
            if (read < (int)headerLen) return false;
            var headerJson = Encoding.UTF8.GetString(headerBuf);

            using var doc = JsonDocument.Parse(headerJson);
            if (!doc.RootElement.TryGetProperty("__metadata__", out var metadata)) return false;
            if (metadata.ValueKind != JsonValueKind.Object) return false;

            // Try ss_sd_model_name first, then modelspec.title
            if (metadata.TryGetProperty("ss_sd_model_name", out var sdName)
                && sdName.ValueKind == JsonValueKind.String)
            {
                modelName = sdName.GetString();
                return !string.IsNullOrEmpty(modelName);
            }
            if (metadata.TryGetProperty("modelspec.title", out var msTitle)
                && msTitle.ValueKind == JsonValueKind.String)
            {
                modelName = msTitle.GetString();
                return !string.IsNullOrEmpty(modelName);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
