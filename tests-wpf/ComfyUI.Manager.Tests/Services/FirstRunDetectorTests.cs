using System;
using System.IO;
using ComfyUI.Manager.Services.FirstRun;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class FirstRunDetectorTests : IDisposable
{
    private readonly string _dir;
    public FirstRunDetectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"firstrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void IsFirstRun_True_WhenSettingsMissing()
    {
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_True_WhenSettingsEmpty()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "");
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_False_WhenSentinelExists()
    {
        File.WriteAllText(Path.Combine(_dir, ".first-run-complete"), "");
        Assert.False(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void IsFirstRun_True_WhenSettingsPresent_NoSentinel()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        // user has settings but never completed wizard → still first run
        Assert.True(FirstRunDetector.IsFirstRun(_dir));
    }

    [Fact]
    public void MarkComplete_WritesSentinel()
    {
        FirstRunDetector.MarkComplete(_dir);
        Assert.True(File.Exists(Path.Combine(_dir, ".first-run-complete")));
        Assert.False(FirstRunDetector.IsFirstRun(_dir));
    }
}
