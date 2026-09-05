using System;
using System.IO;
using ComfyUI.Manager.Services.FirstRun;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x T41:锁 FirstRunDetector 用 config/firstrun.inf INI 协议控制首次运行。
///
/// - 文件缺 → 首次运行(true)
/// - <c>executed=0</c> → 已完成,跳过 wizard(false)
/// - <c>executed=1</c>(或任何非 0 值)→ 强制重新跑 wizard(true)
/// - 文件存在但读不出 <c>executed</c> → 默认首次(true)
/// - MarkComplete 写 executed=0 + clean 旧 .first-run-complete sentinel
///
/// 测试约定:_appDataDir = projectRoot 语义(不含 config 子目录),
/// IsFirstRun/MarkComplete/ResetToForceRun 内部会自己拼 config/。
/// 这跟 App.xaml.cs:146(传 localPaths.Directory,=projectRoot/config,会得到
/// projectRoot/config/config/firstrun.inf 嵌套)和 VM test(line 115-118 显式
/// 用 _appDataDir 语义)两边风格不同 — 测试用 projectRoot 语义因为 API
/// 设计假设调用方传 projectRoot。
/// </summary>
public class FirstRunDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _appDataDir; // = _tempDir (projectRoot 语义)

    public FirstRunDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fr-tests-" + Guid.NewGuid().ToString("N"));
        _configDir = "config";
        // T41 fix:_appDataDir 当 projectRoot,IsFirstRun/MarkComplete 内部拼 config
        _appDataDir = _tempDir;
    }

    /// <summary>helper:写到 IsFirstRun 实际会找的位置 = _appDataDir/config/firstrun.inf</summary>
    private string InfPath => Path.Combine(_appDataDir, _configDir, FirstRunDetector.FirstRunInfName);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void IsFirstRun_NoInfFile_ReturnsTrue()
    {
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_InfFileMissingInConfigDir_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_appDataDir, _configDir));
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_InfFileExecutedZero_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_appDataDir, _configDir));
        File.WriteAllText(InfPath, $"{FirstRunDetector.ExecutedKey}={FirstRunDetector.ExecutedFalse}\n");
        Assert.False(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_InfFileExecutedOne_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_appDataDir, _configDir));
        File.WriteAllText(InfPath, $"{FirstRunDetector.ExecutedKey}=1\n");
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_InfFileOtherKey_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_appDataDir, _configDir));
        File.WriteAllText(InfPath, "version=1.0\nother=value\n");
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_InfFileCorrupted_DefaultsToTrue()
    {
        Directory.CreateDirectory(Path.Combine(_appDataDir, _configDir));
        File.WriteAllText(InfPath, "garbage_no_equals_sign\nonly lines without key=value\n");
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }

    [Fact]
    public void IsFirstRun_DefaultConfigDir_UsesConfig()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        File.WriteAllText(Path.Combine(_tempDir, "config", FirstRunDetector.FirstRunInfName),
            $"{FirstRunDetector.ExecutedKey}=0\n");
        Assert.False(FirstRunDetector.IsFirstRun(_tempDir));
    }

    [Fact]
    public void MarkComplete_WritesInfFile_AndRemovesOldSentinel()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, ".first-run-complete"), "");

        FirstRunDetector.MarkComplete(_tempDir, _configDir);

        Assert.True(File.Exists(InfPath), "firstrun.inf should be written");
        var content = File.ReadAllText(InfPath);
        Assert.Contains($"{FirstRunDetector.ExecutedKey}={FirstRunDetector.ExecutedFalse}", content);

        Assert.False(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));

        var oldSentinel = Path.Combine(_tempDir, ".first-run-complete");
        Assert.False(File.Exists(oldSentinel), "old sentinel should be removed");
    }

    [Fact]
    public void ResetToForceRun_WritesExecutedOne()
    {
        FirstRunDetector.MarkComplete(_tempDir, _configDir);
        Assert.False(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));

        FirstRunDetector.ResetToForceRun(_tempDir, _configDir);
        Assert.True(FirstRunDetector.IsFirstRun(_appDataDir, _configDir));
    }
}
