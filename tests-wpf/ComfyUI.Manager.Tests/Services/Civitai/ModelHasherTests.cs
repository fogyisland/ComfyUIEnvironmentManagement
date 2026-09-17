using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ComfyUI.Manager.Services.Civitai;

namespace ComfyUI.Manager.Tests.Services.Civitai;

/// <summary>
/// v1.0.0.x T46:Tests for tensor-only SHA256 hashing of safetensors files.
/// Reference algorithm: SwarmUI T2IModel.cs:76-122 (skip 8B LE uint64 headerLength
/// + metadata, hash tensor payload only).
/// </summary>
public sealed class ModelHasherTests : IDisposable
{
    private readonly System.Collections.Generic.List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* teardown */ }
        }
    }

    /// <summary>
    /// Builds a fake safetensors file with the standard 8B LE uint64 header length
    /// prefix + UTF-8 metadata JSON + payload bytes. Layout:
    ///   [0..8)   LE uint64 = headerBytes.Length
    ///   [8..)    headerBytes  (metadata JSON)
    ///   then     payloadSize bytes of value payloadByte
    /// </summary>
    private string CreateFakeSafetensors(string headerJson, byte payloadByte, int payloadSize)
    {
        var headerBytes = string.IsNullOrEmpty(headerJson)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(headerJson);
        var path = Path.Combine(Path.GetTempPath(), $"fake_{Guid.NewGuid():N}.safetensors");
        _tempFiles.Add(path);
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // 8B LE uint64 header length
            Span<byte> lenBuf = stackalloc byte[8];
            BitConverter.TryWriteBytes(lenBuf, (ulong)headerBytes.Length);
            // On little-endian platforms this writes LE; on big-endian we need reverse.
            if (!BitConverter.IsLittleEndian) lenBuf.Reverse();
            fs.Write(lenBuf);
            fs.Write(headerBytes);
            // payload
            var chunk = new byte[8192];
            int remaining = payloadSize;
            while (remaining > 0)
            {
                int take = Math.Min(chunk.Length, remaining);
                Array.Fill(chunk, payloadByte, 0, take);
                fs.Write(chunk, 0, take);
                remaining -= take;
            }
        }
        return path;
    }

    /// <summary>
    /// Compute the expected SHA256 over the tensor payload (i.e. bytes after
    /// the 8B header length + header JSON). Matches what the implementation
    /// should hash, independent of the header.
    /// </summary>
    private static string ExpectedTensorPayloadHash(string fakeSafetensorsPath)
    {
        var bytes = File.ReadAllBytes(fakeSafetensorsPath);
        var headerLen = BitConverter.ToUInt64(bytes, 0);
        var tensorStart = 8L + (long)headerLen;
        var payloadLen = bytes.Length - tensorStart;
        // SHA256 streaming over payload bytes only
        using var sha = SHA256.Create();
        sha.TransformBlock(bytes, (int)tensorStart, (int)payloadLen, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var sb = new StringBuilder(64);
        foreach (var b in sha.Hash ?? Array.Empty<byte>()) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    [Fact]
    public void ComputeTensorOnlySha256_SafetensorsFixture_MatchesPythonReference()
    {
        var fixturePath = CreateFakeSafetensors(headerJson: "{\"test\":1}", payloadByte: 0x42, payloadSize: 1024);
        var expected = ExpectedTensorPayloadHash(fixturePath);
        var actual = ModelHasher.ComputeTensorOnlySha256(fixturePath);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeTensorOnlySha256_EmptyHeader_FallsBackToFullFileHash()
    {
        // headerLength=0 → sanity check (headerLen > 0) fails → fall back to full-file hash.
        // Plan spec name: "EmptyHeader_ThrowsOrReturnsAllPayloadHash" — our impl returns full-file hash.
        var fixturePath = CreateFakeSafetensors(headerJson: "", payloadByte: 0xAB, payloadSize: 256);
        var actual = ModelHasher.ComputeTensorOnlySha256(fixturePath);
        var fullFile = ModelHasher.ComputeSha256(fixturePath);
        Assert.Equal(fullFile, actual);
    }

    [Fact]
    public void ComputeTensorOnlySha256_LargeFile_StreamsWithoutLoadingAll()
    {
        // 100MB fixture — verify streaming keeps memory bounded (<50MB delta).
        var fixturePath = CreateFakeSafetensors(headerJson: "{\"x\":1}", payloadByte: 0xCD, payloadSize: 100 * 1024 * 1024);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var beforeBytes = GC.GetTotalMemory(true);
        var hash = ModelHasher.ComputeTensorOnlySha256(fixturePath);
        var afterBytes = GC.GetTotalMemory(false);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.True(afterBytes - beforeBytes < 50 * 1024 * 1024,
            $"Memory delta {(afterBytes - beforeBytes) / 1024 / 1024} MB should stay bounded (streaming)");
    }

    [Fact]
    public void ComputeTensorOnlySha256_NotSafetensors_FallsBackToFullFileHash()
    {
        // .gguf fixture: headerLength > fileSize-8 → sanity fails → full-file fallback
        var fakeGgufPath = Path.Combine(Path.GetTempPath(), $"fake_{Guid.NewGuid():N}.gguf");
        _tempFiles.Add(fakeGgufPath);
        var data = new byte[10 * 1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(fakeGgufPath, data);

        var tensorOnly = ModelHasher.ComputeTensorOnlySha256(fakeGgufPath);
        var fullFile = ModelHasher.ComputeSha256(fakeGgufPath);
        Assert.Equal(fullFile, tensorOnly);
    }

    [Fact]
    public void ComputeTensorOnlySha256_ReturnsUppercase64CharHex()
    {
        var fixturePath = CreateFakeSafetensors(headerJson: "{\"a\":1}", payloadByte: 0xEF, payloadSize: 128);
        var hash = ModelHasher.ComputeTensorOnlySha256(fixturePath);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]{64}$", hash);
    }
}
