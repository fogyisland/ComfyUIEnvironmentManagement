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
    public void Apply_TemplateComfyuiDir_EmptyDefaultsToComfyUITemplate()
    {
        // v1.0.0.x: 改成 seed Templates["ComfyUI"].LocalSourceDir 指向相对路径 "ComfyUI"
        // 不依赖 projectRoot 绝对路径(用户 2026-08-24 反馈)。
        // v1.0.0.x bug #509:不再有 "envTemplates/" 前缀(原 "<system_template_library_dir>/envTemplates/ComfyUI"
        // 多一层嵌套,跟 anchor 拼成 <sys>/envTemplates/ComfyUI 不正确)。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.Templates.ContainsKey("ComfyUI"));
        Assert.Equal("ComfyUI", s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_UserConfiguredPaths_EmptyStaysEmpty()
    {
        // v1.0.0.x #592:EnvsDir 默认 seed 绝对路径(用户原话"本地节点的路径默认获取
        // 当前项目的绝对路径,然后再加上相对路径,这样比较好" + "例如环境默认是
        // 当前目录+envs")。
        // v1.0.0.x #592 扩展:GlobalNodesDir 也走 ResolveAsAbsolute —
        // 给 catalog 的数据库 nodes.db 所在目录,跟其他本地资源路径一致(seed 当前
        // projectRoot + GlobalNodesSubdir 的绝对路径,<projectRoot>/Nodes/)。
        // v1.0.0.x: SystemTemplateLibraryDir / WorkflowsDirectory 改 ResolveAsAbsolute
        // 后空字段也 seed 绝对路径(用户原话"路径设置也和其他一样会自动列出当前的绝对目录")。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Envs", s.EnvsDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Nodes", s.GlobalNodesDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ENVTemplate", s.SystemTemplateLibraryDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Workflow", s.WorkflowsDirectory);
    }

    [Fact]
    public void Apply_DoesNotOverwriteRelativeExistingValues()
    {
        // v1.0.0.x #592 + v1.0.0.x 用户改"路径设置也和其他一样会自动列出当前的绝对目录":
        // EnvsDir / GlobalNodesDir / SystemTemplateLibraryDir / WorkflowsDirectory
        // 都走 ResolveAsAbsolute — 相对路径转绝对。
        // TemplatePythonDir 仍是 Resolve(template-style,空 seed "Python";非空保持)。
        var s = new Settings
        {
            TemplatePythonDir = "E:\\my-python",
            EnvsDir = "my-envs",
            GlobalNodesDir = "shared-nodes",
            SystemTemplateLibraryDir = "my-templates",
            WorkflowsDirectory = "my-workflows",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("E:\\my-python", s.TemplatePythonDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\my-envs", s.EnvsDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\shared-nodes", s.GlobalNodesDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\my-templates", s.SystemTemplateLibraryDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\my-workflows", s.WorkflowsDirectory);
        // v1.0.0.x:空 ComfyUI template entry 被 seed 默认 LocalSourceDir = 相对路径 "ComfyUI"
// (bug #509 修复后去掉 "envTemplates/" 前缀)
        Assert.Equal("ComfyUI", s.Templates["ComfyUI"].LocalSourceDir);
    }

    [Fact]
    public void Apply_MigratesAbsolutePathUnderProjectRoot_PreservedAsAbsolute()
    {
        // v1.0.0.x #592:EnvsDir 走 ResolveAsAbsolute — 绝对路径保留不动(包括 projectRoot 下)。
        // 用户原话"每次启动执行都会扫描当前项目目录"——每次启动 Apply 重算 projectRoot,
        // 老 settings.json 里写的 projectRoot 下绝对路径不需要再剥前缀转相对,直接保留。
        var s = new Settings
        {
            EnvsDir = @"D:\ToolDevelop\ComfyUI\bin\Debug\net8.0-windows\envs",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\bin\Debug\net8.0-windows\envs", s.EnvsDir);
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
        // v1.0.0.x #592:EnvsDir 现在是 ResolveAsAbsolute — 相对路径转绝对。
        // TemplatePythonDir 仍是 Resolve(template-style,空 seed "Python";相对路径转交
        // MigrateOnly 保留原值,不走 MigrateOnly 的 projectRoot 剥前缀,因为它在
        // projectRoot 外("..\external-python"))。
        var s = new Settings
        {
            EnvsDir = "my-envs",  // 用户自定义子目录名 → 转绝对 = projectRoot + "my-envs"
            TemplatePythonDir = "..\\external-python",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\my-envs", s.EnvsDir);
        Assert.Equal("..\\external-python", s.TemplatePythonDir);
    }

    [Fact]
    public void Apply_MigratesLegacySubdirNames_ToPascalCase()
    {
        // v1.0.0.x #592:EnvsDir / LocalNodeDirectory / GlobalNodesDir 现在走
        // ResolveAsAbsolute — MigrateOldSubdirName 迁到 PascalCase 后再转绝对。
        // v1.0.0.x: SystemTemplateLibraryDir / WorkflowsDirectory 也走 ResolveAsAbsolute —
        // 同样 MigrateOldSubdirName → PascalCase 后转绝对。
        // TemplatePythonDir / DefaultModelsDirectory 仍是 Resolve (template-style,空 seed 子目录名)。
        var s = new Settings
        {
            TemplatePythonDir = "python",
            EnvsDir = "envs",
            GlobalNodesDir = "global-nodes",
            LocalNodeDirectory = "local-nodes",
            SystemTemplateLibraryDir = "envtemplate",
            WorkflowsDirectory = "workflows",
            DefaultModelsDirectory = "models",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal("Python", s.TemplatePythonDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Envs", s.EnvsDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Nodes", s.GlobalNodesDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\LocalNodes", s.LocalNodeDirectory);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ENVTemplate", s.SystemTemplateLibraryDir);
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\Workflow", s.WorkflowsDirectory);
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

    // —— v1.0.0 Phase 1:dev 模式 feature flag override ——
    // DEBUG build 下 HuggingFace + ModelScope 默认 enabled=true;workflow 3 source
    // 显式锁 true(防用户手动关后 dev 跳不出页面)。Release build 走 const fold,
    // override 分支 no-op,字段保持 release 默认值。
    //
    // 测试策略:测试程序集默认编译为 DEBUG,所以 IsEnabled=true,验证字段值。
    // Release-mode 行为无法在此验证(需另编译 release 跑),但代码路径可读
    // 保证 const fold 后 override 分支是 dead code。

    [Fact]
    public void Apply_DevMode_EnablesHuggingFaceSource()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.ModelSourceHuggingFaceEnabled);
    }

    [Fact]
    public void Apply_DevMode_EnablesModelScopeSource()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.ModelSourceModelScopeEnabled);
    }

    [Fact]
    public void Apply_DevMode_LocksWorkflowSourcesEnabled()
    {
        // 用户在 dev 关掉某个 workflow source → Apply 强制改回 true
        var s = new Settings
        {
            WorkflowSourceCommunityJsonEnabled = false,
            WorkflowSourceCivitAiEnabled = false,
            WorkflowSourceOpenArtEnabled = false,
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.WorkflowSourceCommunityJsonEnabled);
        Assert.True(s.WorkflowSourceCivitAiEnabled);
        Assert.True(s.WorkflowSourceOpenArtEnabled);
    }

    [Fact]
    public void Apply_DevMode_DoesNotTouchCivitAiSourceAlreadyEnabled()
    {
        // CivitAI 已 release 默认 true,override 不应改其他字段
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.True(s.ModelSourceCivitAiEnabled);
    }

    [Fact]
    public void DevMode_IsEnabled_MatchesDebugConfiguration()
    {
        // 测试程序集编译为 DEBUG → IsEnabled 必须 true;若编译 release 此测试会失败
        // (作为 sanity check 提醒开发者切换配置后跑 full suite)
        Assert.True(DevMode.IsEnabled);
    }

    // —— v1.0.0.x #569 phase 2: SystemTemplateLibraryDir 默认值 ——
    // 用户反馈"新建 env 时模板源路径一样不对,按道理应该 = 项目目录 + envtemplate + 模板路径"。
    // 之前空字段 → TemplatePathResolver fallback 到 AppContext.BaseDirectory(= bin/Debug/...)污染 dev。
    // SettingsDefaults.Apply 现在空字段 seed 相对 "ENVTemplate",resolve 拼出 <projectRoot>/ENVTemplate/<Kind>。

    [Fact]
    public void Apply_SystemTemplateLibraryDir_Empty_DefaultsToAbsoluteEnvTemplate()
    {
        // v1.0.0.x 用户原话"路径设置也和其他一样会自动列出当前的绝对目录" ——
        // SystemTemplateLibraryDir 改 ResolveAsAbsolute,空 → seed 当前 projectRoot +
        // "ENVTemplate" 的绝对路径(<projectRoot>/ENVTemplate/)。
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ENVTemplate", s.SystemTemplateLibraryDir);
    }

    [Fact]
    public void Apply_SystemTemplateLibraryDir_LegacyLowercase_MigratedToEnvTemplate()
    {
        // 老 settings 里大/小写不一致的 "envtemplate" → 迁到 "ENVTemplate"(再转绝对)
        var s = new Settings
        {
            SystemTemplateLibraryDir = "envtemplate",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\ENVTemplate", s.SystemTemplateLibraryDir);
    }

    [Fact]
    public void Apply_SystemTemplateLibraryDir_PreservesUserCustomRelativePath()
    {
        // 用户主动填了相对路径(非默认)→ 转绝对(跟 EnvsDir 等本地资源路径一致)
        var s = new Settings
        {
            SystemTemplateLibraryDir = "my-templates",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\my-templates", s.SystemTemplateLibraryDir);
    }

    [Fact]
    public void Apply_SystemTemplateLibraryDir_AbsoluteUnderProjectRoot_PreservedAsAbsolute()
    {
        // ResolveAsAbsolute 对所有绝对路径都保留(包括 projectRoot 下),
        // 不做"projectRoot 下 → 转相对"剥前缀(用户原意:每次启动重新算 projectRoot)。
        var s = new Settings
        {
            SystemTemplateLibraryDir = @"D:\ToolDevelop\ComfyUI\old-ENVTemplate",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\old-ENVTemplate", s.SystemTemplateLibraryDir);
    }

    [Fact]
    public void Apply_SystemTemplateLibraryDir_AbsoluteOutsideProjectRoot_Preserved()
    {
        // 用户故意选了别处的绝对路径 → 保留(不强行改)
        var s = new Settings
        {
            SystemTemplateLibraryDir = @"E:\external\env-templates",
        };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"E:\external\env-templates", s.SystemTemplateLibraryDir);
    }

    // —— v1.0.0.x #592:EnvsDir / LocalNodeDirectory / LocalNodesDirectory 走
    // ResolveAsAbsolute — 空 seed 绝对,相对转绝对,绝对保留。

    [Fact]
    public void Apply_LocalNodesDirectory_Empty_DefaultsToAbsolutePath()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        // 用户原话"本地节点的路径默认获取当前项目的绝对路径,然后再加上相对路径,
        // 这样比较好" + "例如环境默认是 当前目录+envs"。
        Assert.Equal(@"D:\ToolDevelop\ComfyUI\localnodes", s.LocalNodesDirectory);
    }

    [Fact]
    public void Apply_LocalNodesDirectory_RelativePath_ConvertsToAbsolutePath()
    {
        var s = new Settings { LocalNodesDirectory = "shared-localnodes" };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\shared-localnodes", s.LocalNodesDirectory);
    }

    [Fact]
    public void Apply_LocalNodesDirectory_AbsolutePathInsideProjectRoot_PreservedAsAbsolute()
    {
        // 跟 Resolve / MigrateOnly 不同 — ResolveAsAbsolute 对所有绝对路径都保留,
        // 不做"projectRoot 下 → 转相对"剥前缀(用户原意:每次启动重新算 projectRoot)。
        var s = new Settings { LocalNodesDirectory = @"D:\ToolDevelop\ComfyUI\shared-localnodes" };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\shared-localnodes", s.LocalNodesDirectory);
    }

    [Fact]
    public void Apply_LocalNodeDirectory_Empty_DefaultsToAbsolutePath()
    {
        var s = new Settings();

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\LocalNodes", s.LocalNodeDirectory);
    }

    [Fact]
    public void Apply_LocalNodesDirectory_AbsolutePathOutsideProjectRoot_KeptAsIs()
    {
        var s = new Settings { LocalNodesDirectory = @"E:\external\local-nodes" };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"E:\external\local-nodes", s.LocalNodesDirectory);
    }

    [Fact]
    public void Apply_LocalNodesDirectory_LegacyKebabCase_MigratedAndMadeAbsolute()
    {
        // v1.0.0.x #577 早期 seed 写的是 "localnodes"(全小写)— MigrateOldSubdirName
        // 早返回("localnodes" 不在迁表里),然后 ResolveAsAbsolute 转绝对。
        var s = new Settings { LocalNodesDirectory = "localnodes" };

        SettingsDefaults.Apply(s, ProjectRoot);

        Assert.Equal(@"D:\ToolDevelop\ComfyUI\localnodes", s.LocalNodesDirectory);
    }
}