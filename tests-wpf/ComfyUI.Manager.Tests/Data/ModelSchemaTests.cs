using ComfyUI.Manager.Data;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:验证 InitModelSchema(model.db)在 T45 3 表基础上
/// 新增 2 张表 ——
///   • model_stars    (Star 收藏 PK source_path)
///   • model_settings (UI 状态 K-V PK key)
/// 老 DB CREATE IF NOT EXISTS 自动迁移,新 DB 走同一路径。
/// </summary>
public class ModelSchemaTests
{
    [Fact]
    public void InitModelSchema_CreatesModelStarsTable()
    {
        using var db = new TestDb();
        using var conn = db.ModelFactory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='model_stars'";
        var result = cmd.ExecuteScalar();
        Assert.Equal("model_stars", result);
    }

    [Fact]
    public void InitModelSchema_CreatesModelSettingsTable()
    {
        using var db = new TestDb();
        using var conn = db.ModelFactory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='model_settings'";
        var result = cmd.ExecuteScalar();
        Assert.Equal("model_settings", result);
    }
}
