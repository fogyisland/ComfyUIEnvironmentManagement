using System.Text.Json;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

public class UiPreferencesSerializationTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var orig = new UiPreferences
        {
            WindowWidth = 1024,
            WindowHeight = 768,
            WindowLeft = 100,
            WindowTop = 50,
            WindowMaximized = true,
            SidebarVisible = false,
            LastSelectedEnvId = "env-abc",
            // v1.0.0.x (2026-09-15) feat/nodelist-market-redesign (T43):LastViewName 持久化值
            // "Catalog" → "Nodelist"(MainViewModel.ResolveCurrentViewName switch 改名)。
            LastViewName = "Nodelist",
        };
        var json = JsonSerializer.Serialize(orig, Opts);
        var back = JsonSerializer.Deserialize<UiPreferences>(json, Opts)!;
        Assert.Equal(1024, back.WindowWidth);
        Assert.Equal(768, back.WindowHeight);
        Assert.Equal(100, back.WindowLeft);
        Assert.Equal(50, back.WindowTop);
        Assert.True(back.WindowMaximized);
        Assert.False(back.SidebarVisible);
        Assert.Equal("env-abc", back.LastSelectedEnvId);
        Assert.Equal("Nodelist", back.LastViewName);
    }

    [Fact]
    public void Deserialize_AllFieldsNull_ReturnsDefaults()
    {
        var back = JsonSerializer.Deserialize<UiPreferences>("{}", Opts)!;
        Assert.Null(back.WindowWidth);
        Assert.Null(back.WindowLeft);
        Assert.False(back.WindowMaximized);
        Assert.True(back.SidebarVisible);  // default true
        Assert.Null(back.LastSelectedEnvId);
    }
}
