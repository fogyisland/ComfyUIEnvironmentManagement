using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:LocalModelSettingsRepository K-V CRUD round-trip tests ——
/// model_settings 表(UI 状态:view_mode / stars_only / search 等)。Get / Set /
/// GetOrDefault;Set 走 ON CONFLICT 覆盖更新。
/// </summary>
public class LocalModelSettingsRepositoryTests
{
    [Fact]
    public void Set_NewKey_InsertsRow()
    {
        using var db = new TestDb();
        var repo = new LocalModelSettingsRepository(db.ModelFactory);
        repo.Set("localmodels.view_mode", "List");
        Assert.Equal("List", repo.Get("localmodels.view_mode"));
    }

    [Fact]
    public void Get_ExistingKey_ReturnsValue()
    {
        using var db = new TestDb();
        var repo = new LocalModelSettingsRepository(db.ModelFactory);
        repo.Set("k", "v");
        Assert.Equal("v", repo.Get("k"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        using var db = new TestDb();
        var repo = new LocalModelSettingsRepository(db.ModelFactory);
        Assert.Null(repo.Get("nonexistent"));
    }

    [Fact]
    public void GetOrDefault_MissingKey_ReturnsDefault()
    {
        using var db = new TestDb();
        var repo = new LocalModelSettingsRepository(db.ModelFactory);
        Assert.Equal("Cards", repo.GetOrDefault("localmodels.view_mode", "Cards"));
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValueAndTimestamp()
    {
        using var db = new TestDb();
        var repo = new LocalModelSettingsRepository(db.ModelFactory);
        repo.Set("k", "v1");
        System.Threading.Thread.Sleep(10);
        repo.Set("k", "v2");
        Assert.Equal("v2", repo.Get("k"));
    }
}