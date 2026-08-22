using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class SettingsDefaultsTests
{
    private const string ProjectRoot = @"D:\ToolDevelop\ComfyUI";

    [Fact]
    public void Apply_TemplatePythonDir_EmptyDefaultsToPython()
    {
        // template paths:空字段填默认子目录名(指向 package 自带的 portable Python/)
        // v1.0.0:子目录统一 PascalCase → "Python"(而非 v0.6.x 的 "python")
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("Python", s.TemplatePythonDir);
    }

    [Fact]
    public void Apply_TemplateComfyuiDir_EmptyDefaultsToComfyUI()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);
    }

    [Fact]
    public void Apply_UserConfiguredPaths_EmptyStaysEmpty()
    {
        // EnvsDir / GlobalNodesDir 默认保持空(用户主动管理,服务层在使用时报错)
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("", s.EnvsDir);
        Assert.Equal("", s.GlobalNodesDir);
    }

    [Fact]
    public void Apply_DoesNotOverwriteRelativeExistingValues()
    {
        // 用户已经填了相对路径 → 不动
        var s = new Settings
        {
            TemplatePythonDir = "E:\\my-python",
            EnvsDir = "my-envs",
            GlobalNodesDir = "shared-nodes",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("E:\\my-python", s.TemplatePythonDir);
        Assert.Equal("my-envs", s.EnvsDir);
        Assert.Equal("shared-nodes", s.GlobalNodesDir);
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);   // 空字段填默认
    }

    [Fact]
    public void Apply_MigratesAbsolutePathUnderProjectRoot_ToRelative()
    {
        // 兼容旧 settings.json:绝对路径若落在 projectRoot 下,转相对(剥掉前缀)
        var s = new Settings
        {
            EnvsDir = @"D:\ToolDevelop\ComfyUI\bin\Debug\net8.0-windows\envs",
            TemplateComfyuiDir = @"D:\ToolDevelop\ComfyUI\ComfyUI",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"bin\Debug\net8.0-windows\envs", s.EnvsDir);
        Assert.Equal("ComfyUI", s.TemplateComfyuiDir);
    }

    [Fact]
    public void Apply_PreservesAbsolutePathOutsideProjectRoot()
    {
        // 用户故意选别处的绝对路径(如外部盘) → 保留,不强行改
        var s = new Settings
        {
            EnvsDir = @"E:\external\envs",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"E:\external\envs", s.EnvsDir);
    }

    [Fact]
    public void Apply_NullSettings_NoOp()
    {
        SettingsDefaults.Apply(null!, ProjectRoot);
    }

    [Fact]
    public void Apply_KeepsRelativePathUntouched()
    {
        // 设置页填了相对路径,Apply 不重新格式化或加 ../ 前缀
        // v1.0.0:子目录统一 PascalCase,旧值 "envs" 会被 MigrateOldSubdirName 迁到 "Envs"
        var s = new Settings
        {
            EnvsDir = "my-envs",  // 用户自定义子目录名 → 不在迁移表里,保持不变
            TemplatePythonDir = "..\\external-python",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("my-envs", s.EnvsDir);
        Assert.Equal("..\\external-python", s.TemplatePythonDir);
    }

    [Fact]
    public void Apply_MigratesLegacySubdirNames_ToPascalCase()
    {
        // v1.0.0:老 settings.json 里写过的旧子目录名(全小写 / kebab-case)
        // 一次性迁到 PascalCase。其它用户自定义子目录名不受影响。
        var s = new Settings
        {
            TemplatePythonDir = "python",
            EnvsDir = "envs",
            GlobalNodesDir = "global-nodes",
            LocalNodeDirectory = "local-nodes",
            WorkflowsDirectory = "workflows",
            DefaultModelsDirectory = "models",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("Python", s.TemplatePythonDir);
        Assert.Equal("Envs", s.EnvsDir);
        Assert.Equal("Nodes", s.GlobalNodesDir);
        Assert.Equal("LocalNodes", s.LocalNodeDirectory);
        Assert.Equal("Workflow", s.WorkflowsDirectory);
        Assert.Equal("Models", s.DefaultModelsDirectory);
    }

    [Fact]
    public void Apply_QuerySources_EmptyGetsDefault()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.QuerySources);
        Assert.Equal("comfyui manager", s.QuerySources[0].Name);
        Assert.Equal(SettingsDefaults.DefaultQuerySourceUrl, s.QuerySources[0].Url);
    }

    [Fact]
    public void Apply_DownloadSources_EmptyGetsDefault()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.DownloadSources);
        Assert.Equal("comfyui manager", s.DownloadSources[0].Name);
        Assert.Equal(SettingsDefaults.DefaultDownloadSourceUrl, s.DownloadSources[0].Url);
    }

    [Fact]
    public void Apply_ActiveQuerySourceName_EmptyFallbacksToFirst()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("comfyui manager", s.ActiveQuerySourceName);
    }

    [Fact]
    public void Apply_ActiveDownloadSourceName_EmptyFallbacksToFirst()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("comfyui manager", s.ActiveDownloadSourceName);
    }

    [Fact]
    public void Apply_ExistingQuerySources_NotOverwritten()
    {
        // 用户已有自定义 query sources → 不覆盖
        var s = new Settings
        {
            QuerySources = new List<NodeSource>
            {
                new() { Name = "my-mirror", Url = "https://my-mirror/catalog.json" },
            },
            ActiveQuerySourceName = "my-mirror",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Single(s.QuerySources);
        Assert.Equal("my-mirror", s.QuerySources[0].Name);
        Assert.Equal("my-mirror", s.ActiveQuerySourceName);
    }

    [Fact]
    public void Apply_CatalogPageSize_ZeroOrNegativeGetsDefault()
    {
        var s = new Settings { CatalogPageSize = 0 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(20, s.CatalogPageSize);
    }

    [Fact]
    public void Apply_CatalogPageSize_NegativeGetsDefault()
    {
        var s = new Settings { CatalogPageSize = -1 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(20, s.CatalogPageSize);
    }

    [Fact]
    public void Apply_CatalogViewMode_DefaultsToList_OnFreshSettings()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(CatalogViewMode.List, s.CatalogViewMode);
    }

    [Fact]
    public void Apply_CatalogPageSize_PositiveValuePreserved()
    {
        var s = new Settings { CatalogPageSize = 50 };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(50, s.CatalogPageSize);
    }

    // —— v0.6.5.6 hotfix:migration layout probe ——
    // 修复前:migration 硬编码 <dir>/<version>/python.exe,假定 multi-version layout。
    // 实际项目 portable python 是 flat layout(python.exe 直接在根),合成出死路径
    // → VENV_PYTHON_MISSING 报错。修复:File.Exists 探测两种 layout。
    // multi-version 优先(spec 原意),fallback 到 flat,都不在就跳过合成。

    [Fact]
    public void Apply_Migration_MultiVersionLayout_SynthesizesVersionedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-mig-mv-" + Guid.NewGuid().ToString("N")[..8]);
        var versionDir = Path.Combine(tempDir, "3.10");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "python.exe"), "fake");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("3.10", s.PythonInterpreters[0].Name);
            Assert.Equal(Path.Combine(tempDir, "3.10", "python.exe"), s.PythonInterpreters[0].Path);
            Assert.Equal("3.10", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_Migration_FlatLayout_SynthesizesFlatPath()
    {
        // 本项目实际 portable python venv 布局:python.exe 直接在 python/ 根,无版本子目录
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-mig-flat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "python.exe"), "fake");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("3.10", s.PythonInterpreters[0].Name);
            Assert.Equal(Path.Combine(tempDir, "python.exe"), s.PythonInterpreters[0].Path);
            Assert.Equal("3.10", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_Migration_NeitherLayoutExists_SkipsSynthesis()
    {
        // 都不存在 → 不合成死路径,留空让用户去 Settings → Browse 添加
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-mig-none-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            Assert.Empty(s.PythonInterpreters);
            Assert.Equal("", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_Migration_MultiVersionPreferredOverFlat()
    {
        // 两 layout 都存在 → multi-version 优先(spec 原意)
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-mig-both-" + Guid.NewGuid().ToString("N")[..8]);
        var versionDir = Path.Combine(tempDir, "3.10");
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "python.exe"), "fake");
        File.WriteAllText(Path.Combine(tempDir, "python.exe"), "fake");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal(Path.Combine(tempDir, "3.10", "python.exe"), s.PythonInterpreters[0].Path);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_CleanupV056BadMigration_RemovesBrokenEntryAndReRuns()
    {
        // v0.6.5.6 hotfix:用户 settings.json 里的坏条目(<dir>/<version>/python.exe 不存在)
        // → 启动时精准清掉,重新走 migration 合成正确的 flat-path 条目
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "python.exe"), "fake");
        try
        {
            var brokenPath = Path.Combine(tempDir, "3.10", "python.exe");   // 不存在
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
                PythonInterpreters = new List<PythonInterpreter>
                {
                    new() { Name = "3.10", Path = brokenPath },   // v0.6.5.6 留下的死条目
                },
                ActivePythonInterpreterName = "3.10",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            // 死条目被精准清掉,migration 重新合成正确的 flat-path 条目
            Assert.Single(s.PythonInterpreters);
            Assert.Equal(Path.Combine(tempDir, "python.exe"), s.PythonInterpreters[0].Path);
            Assert.Equal("3.10", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_CleanupV056BadMigration_LeavesUserAddedEntryAlone()
    {
        // 用户手动 Browse 加的条目(路径不等于合成路径)→ 不被 cleanup 误删
        var tempDir = Path.Combine(Path.GetTempPath(), "cmgr-cleanup-keep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var userPy = Path.Combine(tempDir, "user-py.exe");
        File.WriteAllText(userPy, "fake");
        try
        {
            var s = new Settings
            {
                TemplatePythonDir = tempDir,
                DefaultPythonVersion = "3.10",
                PythonInterpreters = new List<PythonInterpreter>
                {
                    new() { Name = "user-added", Path = userPy },   // 路径不等于合成路径
                },
                ActivePythonInterpreterName = "user-added",
            };

            SettingsDefaults.Apply(s, ProjectRoot);

            Assert.Single(s.PythonInterpreters);
            Assert.Equal("user-added", s.PythonInterpreters[0].Name);
            Assert.Equal(userPy, s.PythonInterpreters[0].Path);
            Assert.Equal("user-added", s.ActivePythonInterpreterName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}