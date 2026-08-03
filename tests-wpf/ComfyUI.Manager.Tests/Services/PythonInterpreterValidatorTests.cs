using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class PythonInterpreterValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsValid_WhenPathIsPythonExe()
    {
        var py = ResolveSystemPython();
        if (py is null) return;  // 机器没 Python 跳过 happy path
        var sut = new PythonInterpreterValidator();

        var result = await sut.ValidateAsync(py);

        Assert.True(result.IsValid, $"Expected valid Python, got Error={result.Error}");
        Assert.Matches(@"^\d+\.\d+(\.\d+)?$", result.Version);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenPathMissing()
    {
        var sut = new PythonInterpreterValidator();
        var bogus = Path.Combine(Path.GetTempPath(), "definitely_does_not_exist_python_xyz.exe");

        var result = await sut.ValidateAsync(bogus);

        Assert.False(result.IsValid);
        Assert.Contains("不存在", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenPathNotPython()
    {
        var notepad = ResolveNotepad();
        if (notepad is null) return;
        var sut = new PythonInterpreterValidator();

        var result = await sut.ValidateAsync(notepad);

        Assert.False(result.IsValid);
        Assert.Contains("Python", result.Error);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_OnTimeout()
    {
        var py = ResolveSystemPython() ?? "python";
        var sut = new PythonInterpreterValidator();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await sut.ValidateAsync(py, cts.Token);

        Assert.False(result.IsValid);
        Assert.True(result.Error == "超时" || result.Error == "无法启动进程",
            $"Expected timeout/cancelled error, got: {result.Error}");
    }

    [Fact]
    public async Task ValidateAsync_DoesNotThrow_OnFailure()
    {
        var sut = new PythonInterpreterValidator();
        var dir = Path.GetTempPath();

        var result = await sut.ValidateAsync(dir);
        Assert.False(result.IsValid);
    }

    private static string? ResolveSystemPython()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "py.exe", "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            var path = FindOnPath(c);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? ResolveNotepad()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        return Path.Combine(Environment.SystemDirectory, "notepad.exe");
    }

    private static string? FindOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
