using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ComfyUI.Manager.Services.Civitai;

/// <summary>
/// v1.0.0:Streaming SHA256 for large model files (2-7GB).
/// Reads in 1MB chunks to avoid loading whole file into memory.
/// Returns uppercase hex string (64 chars).
/// </summary>
public static class ModelHasher
{
    private const int BufferSize = 1024 * 1024; // 1 MB

    public static string ComputeSha256(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath)) throw new ArgumentNullException(nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("Model file not found", filePath);

        using var sha = SHA256.Create();
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: false);
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer, 0, BufferSize)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hashBytes = sha.Hash ?? Array.Empty<byte>();
        var sb = new StringBuilder(64);
        foreach (var b in hashBytes)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
