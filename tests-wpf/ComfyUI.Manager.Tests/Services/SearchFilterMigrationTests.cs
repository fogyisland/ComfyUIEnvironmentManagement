using ComfyUI.Manager.Data;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:验证 T46 → T47 一次性迁移函数。
/// 启动时把老 Settings.LocalModelsFilter 写到 model_settings.localmodels.search,
/// 然后清老字段(避免双写)。
/// </summary>
public class SearchFilterMigrationTests
{
    [Fact]
    public void Migration_FromSettingsFilter_WritesAndClears()
    {
        // arrange:新 model_settings DB + 老 filter 有值
        using var db = new TestDb();
        var settings = new LocalModelSettingsRepository(db.ModelFactory);

        string? oldFilter = "anime";
        bool cleared = false;
        void ClearOld(string? _) => cleared = true;

        // act
        SearchFilterMigration.Migrate(settings, oldFilter, ClearOld);

        // assert:新 settings 表有 search key + 老 filter 被清
        Assert.Equal("anime", settings.Get("localmodels.search"));
        Assert.True(cleared);
    }

    [Fact]
    public void Migration_EmptyOldFilter_NoOpAndDoesNotClear()
    {
        using var db = new TestDb();
        var settings = new LocalModelSettingsRepository(db.ModelFactory);

        string? oldFilter = "";
        bool cleared = false;
        void ClearOld(string? _) => cleared = true;

        SearchFilterMigration.Migrate(settings, oldFilter, ClearOld);

        Assert.Null(settings.Get("localmodels.search"));
        Assert.False(cleared);
    }

    [Fact]
    public void Migration_NullOldFilter_NoOpAndDoesNotClear()
    {
        using var db = new TestDb();
        var settings = new LocalModelSettingsRepository(db.ModelFactory);

        string? oldFilter = null;
        bool cleared = false;
        void ClearOld(string? _) => cleared = true;

        SearchFilterMigration.Migrate(settings, oldFilter, ClearOld);

        Assert.Null(settings.Get("localmodels.search"));
        Assert.False(cleared);
    }
}