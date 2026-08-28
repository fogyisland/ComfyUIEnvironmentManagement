using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0 multi-template T3: Environment.TemplateKind + TemplateConfigSnapshot
/// 字段持久化 + 老行 backfill 到 ComfyUI 默认 template 的 snapshot。
/// </summary>
public class EnvironmentRepositoryTemplateKindTests
{
    [Fact]
    public void ListAll_OldRow_DefaultsToComfyUIKindAndSnapshot()
    {
        // 老 DB schema:没有 template_kind / template_config_snapshot 列,模拟 v1.0.0 升级前。
        // 走 db.Factory.Open() 触发 EnsureColumn 加上新列,然后 INSERT 老格式行(只填非新列),
        // 让 ListAll 时 repo Read 兜底 backfill。
        using var db = new TestDb();
        var factory = db.Factory;
        using (var conn = factory.Open())
        {
            // 老 schema 风格:不写 template_kind / template_config_snapshot,让 DEFAULT 兜底 +
            // repo Read 的 backfill 补 TemplateConfigSnapshot。
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO environments
                    (id, name, root_path, comfyui_layout, comfyui_source,
                     status, base_python_path, python_version)
                VALUES
                    ('old-env', 'oldEnv', '/envs/old', 'isolated', '/old/comfyui',
                     'stopped', '/usr/bin/python', '3.10')";
            cmd.ExecuteNonQuery();
        }

        var repo = new EnvironmentRepository(factory);
        var envs = repo.ListAll();

        var old = Assert.Single(envs);
        Assert.Equal("oldEnv", old.Name);
        Assert.Equal("ComfyUI", old.TemplateKind);
        Assert.NotNull(old.TemplateConfigSnapshot);
        Assert.Equal("main.py", old.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("models", old.TemplateConfigSnapshot.ModelsSubdir);
    }

    [Fact]
    public void Upsert_ThenListAll_PreservesTemplateKindAndSnapshot()
    {
        // v1.0.0.x: A1111 模板已下线,但 TemplateKind 字段仍是 TEXT,允许任意 kind 值。
        // 这里用 "Forge" 作 fixture —— A1111 的"近亲"kind,跟 ComfyUI 一样是
        // shipped local built-in,但走不同的 pre-flight 路径(RequirementsInstaller
        // dispatch),用来测 EnvRepository 持久化层面对任意非 ComfyUI kind 的 round-trip。
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var snapshot = new TemplateConfig
        {
            Name = "Forge",
            Kind = "Forge",
            LocalSourceDir = "Templates/Forge",
            EntryScript = "webui.py",
            EntryArgs = "--port {port} --api",
            ModelsSubdir = "models/Stable-diffusion",
        };
        var env = new Environment
        {
            Id = "new-env",
            Name = "newEnv",
            RootPath = "/envs/new",
            ComfyuiLayout = "isolated",
            Status = "stopped",
            BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10",
            Port = 9001,
            TemplateKind = "Forge",
            TemplateConfigSnapshot = snapshot,
        };
        repo.Upsert(env);

        var loaded = repo.ListAll();
        var found = loaded.Find(e => e.Id == "new-env");
        Assert.NotNull(found);
        Assert.Equal("Forge", found!.TemplateKind);
        Assert.Equal("webui.py", found.TemplateConfigSnapshot!.EntryScript);
        Assert.Equal("models/Stable-diffusion", found.TemplateConfigSnapshot.ModelsSubdir);
    }

    [Fact]
    public void Upsert_OverwritesTemplateKindAndSnapshot_OnConflict()
    {
        using var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var snapshot = new TemplateConfig { Kind = "ComfyUI", EntryScript = "main.py" };
        var env = new Environment
        {
            Id = "upd-env",
            Name = "updEnv",
            RootPath = "/envs/upd",
            ComfyuiLayout = "isolated",
            Status = "running",
            BasePythonPath = "/usr/bin/python",
            PythonVersion = "3.10",
            TemplateKind = "ComfyUI",
            TemplateConfigSnapshot = snapshot,
        };
        repo.Upsert(env);
        // 改 status 再 upsert — 应保留 TemplateKind / Snapshot
        env.Status = "stopped";
        repo.Upsert(env);

        var loaded = repo.ListAll();
        var found = loaded.Find(e => e.Id == "upd-env")!;
        Assert.Equal("stopped", found.Status);
        Assert.Equal("ComfyUI", found.TemplateKind);
        Assert.Equal("main.py", found.TemplateConfigSnapshot!.EntryScript);
    }
}
