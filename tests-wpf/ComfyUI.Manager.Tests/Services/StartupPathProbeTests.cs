using System.IO;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x #594:StartupPathProbe 检测规则测试。覆盖三类场景:
/// - 空字段 → 不报
/// - 相对路径 → 拼 projectRoot 判 Exists
/// - 绝对路径(在 projectRoot 下 / 在别处)→ 直接判 Exists
/// </summary>
public class StartupPathProbeTests
{
    private static Settings NewBareSettings()
    {
        var s = new Settings();
        // 显式清空所有 path 字段 + Templates(避免其他 test fixture 污染)
        s.TemplatePythonDir = "";
        s.SystemTemplateLibraryDir = "";
        s.EnvsDir = "";
        s.GlobalNodesDir = "";
        s.LocalNodeDirectory = "";
        s.LocalNodesDirectory = "";
        s.DefaultModelsDirectory = "";
        s.WorkflowsDirectory = "";
        s.LogDirectory = "";
        s.Templates.Clear();
        return s;
    }

    /// <summary>
    /// Seed 8 个内置 built-in 模板(LocalSourceDir 用默认相对路径 = kind name)。
    /// 模拟真实 SettingsDefaults.Apply 跑完后的状态。
    /// </summary>
    private static void SeedBuiltInTemplates(Settings s, params string[] kinds)
    {
        var all = new[] { "ComfyUI", "A1111", "Forge", "SwarmUI", "OpenVoice", "Whisper", "CoquiTTS", "Bark" };
        foreach (var k in kinds.Length == 0 ? all : kinds)
        {
            if (!s.Templates.ContainsKey(k))
                s.Templates[k] = new TemplateConfig();
            s.Templates[k].LocalSourceDir = k;  // 默认相对路径 = kind name
        }
    }

    private static string MakeTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), $"probe-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void Detect_EmptyFields_ReturnsEmpty()
    {
        var s = NewBareSettings();
        var result = StartupPathProbe.Detect(s, MakeTempDir());
        Assert.Empty(result);
    }

    [Fact]
    public void Detect_RelativePathExists_NotFlagged()
    {
        var root = MakeTempDir();
        var existing = Path.Combine(root, "Envs");
        Directory.CreateDirectory(existing);

        var s = NewBareSettings();
        s.EnvsDir = "Envs";  // 相对路径,Exists

        var result = StartupPathProbe.Detect(s, root);
        Assert.DoesNotContain(result, i => i.Label == "EnvsDir");
    }

    [Fact]
    public void Detect_RelativePathMissing_Flagged()
    {
        var root = MakeTempDir();
        // root/Envs 不存在

        var s = NewBareSettings();
        s.EnvsDir = "Envs";  // 相对路径,不 Exists

        var result = StartupPathProbe.Detect(s, root);
        var item = Assert.Single(result, i => i.Label == "EnvsDir");
        Assert.Equal(Path.Combine(root, "Envs"), item.CurrentValue);
        Assert.Equal(Path.Combine(root, "Envs"), item.RecommendedValue);  // 推荐值相同
    }

    [Fact]
    public void Detect_AbsolutePathInProjectRootExists_NotFlagged()
    {
        var root = MakeTempDir();
        var existing = Path.Combine(root, "LocalNodes");
        Directory.CreateDirectory(existing);

        var s = NewBareSettings();
        s.LocalNodeDirectory = existing;  // 绝对路径 + 在 projectRoot 下 + Exists

        var result = StartupPathProbe.Detect(s, root);
        Assert.DoesNotContain(result, i => i.Label == "LocalNodeDirectory");
    }

    [Fact]
    public void Detect_AbsolutePathInProjectRootMissing_Flagged()
    {
        // 模拟"程序文件夹被搬走但 settings 指向老路径"—— 老路径在另一个 projectRoot 下
        var oldRoot = MakeTempDir();
        var newRoot = MakeTempDir();  // 搬到新位置
        // 不创建 LocalNodes,模拟子目录已丢失

        var s = NewBareSettings();
        s.LocalNodeDirectory = Path.Combine(oldRoot, "LocalNodes");  // 绝对路径 + 不在新 projectRoot 下 + 不 Exists

        var result = StartupPathProbe.Detect(s, newRoot);
        var item = Assert.Single(result, i => i.Label == "LocalNodeDirectory");
        Assert.Equal(Path.Combine(oldRoot, "LocalNodes"), item.CurrentValue);
        Assert.Equal(Path.Combine(newRoot, "LocalNodes"), item.RecommendedValue);  // 推荐新路径
    }

    [Fact]
    public void Detect_AbsolutePathOutsideProjectRootExists_NotFlagged()
    {
        // 用户故意配在别处,仍存在 → 不报
        var root = MakeTempDir();
        var external = MakeTempDir();
        var models = Path.Combine(external, "Models");
        Directory.CreateDirectory(models);

        var s = NewBareSettings();
        s.DefaultModelsDirectory = models;

        var result = StartupPathProbe.Detect(s, root);
        Assert.DoesNotContain(result, i => i.Label == "DefaultModelsDirectory");
    }

    [Fact]
    public void Detect_AbsolutePathOutsideProjectRootMissing_Flagged()
    {
        // 用户故意配在别处,但别处目录被删了 → 报(经典搬盘符场景)
        var root = MakeTempDir();
        var external = MakeTempDir();
        // external/Models 不创建

        var s = NewBareSettings();
        s.DefaultModelsDirectory = Path.Combine(external, "Models");

        var result = StartupPathProbe.Detect(s, root);
        var item = Assert.Single(result, i => i.Label == "DefaultModelsDirectory");
        Assert.Equal(Path.Combine(external, "Models"), item.CurrentValue);
        Assert.Equal(Path.Combine(root, "Models"), item.RecommendedValue);
    }

    [Fact]
    public void Detect_TemplateLocalSourceDir_UserCustomizedMissing_Flagged()
    {
        // v1.0.0.x hotfix (2026-08-27):用户主动改 LocalSourceDir 配错 → 仍报。
        // 默认 seed (== kind 名) 的 missing 不报,见下一个 test。
        var root = MakeTempDir();
        var s = NewBareSettings();
        s.SystemTemplateLibraryDir = "ENVTemplate";
        SeedBuiltInTemplates(s);  // seed 8 个内置模板

        // 把 ComfyUI 的 LocalSourceDir 改成用户自定义(非默认 seed)且不存在的路径
        s.Templates["ComfyUI"].LocalSourceDir = "D:\\NonExistent\\Path\\ForComfyUI";

        var result = StartupPathProbe.Detect(s, root);
        Assert.Contains(result, i => i.Label == "Template:ComfyUI.LocalSourceDir");
        var comfyItem = result.Single(i => i.Label == "Template:ComfyUI.LocalSourceDir");
        Assert.Equal(Path.Combine(root, "ComfyUI"), comfyItem.RecommendedValue);
    }

    [Fact]
    public void Detect_BuiltinTemplate_DefaultSeedMissing_NotFlagged()
    {
        // v1.0.0.x hotfix (2026-08-27):8 个 built-in 模板 LocalSourceDir 是默认 seed
        // (== kind 名),目录不存在 = 用户压根没下载,不是路径错位。不该 flag。
        var root = MakeTempDir();
        var s = NewBareSettings();
        s.SystemTemplateLibraryDir = "ENVTemplate";
        SeedBuiltInTemplates(s);  // 8 个全 seed,但一个 ENVTemplate/{Kind}/ 都没创建

        var result = StartupPathProbe.Detect(s, root);
        // 8 个全不该 flag
        Assert.DoesNotContain(result, i => i.Label.StartsWith("Template:"));
    }

    [Fact]
    public void Detect_TemplateLocalSourceDir_Exists_NotFlagged()
    {
        var root = MakeTempDir();
        var comfyDir = Path.Combine(root, "ENVTemplate", "ComfyUI");
        Directory.CreateDirectory(comfyDir);

        var s = NewBareSettings();
        s.SystemTemplateLibraryDir = "ENVTemplate";
        SeedBuiltInTemplates(s);  // seed 8 个,但只有 ComfyUI 实际存在目录

        var result = StartupPathProbe.Detect(s, root);
        Assert.DoesNotContain(result, i => i.Label == "Template:ComfyUI.LocalSourceDir");
    }

    [Fact]
    public void Detect_LogDirectory_MissingRelative_Flagged()
    {
        var root = MakeTempDir();
        // root/Logs 不创建
        var s = NewBareSettings();
        s.LogDirectory = "Logs";

        var result = StartupPathProbe.Detect(s, root);
        var item = Assert.Single(result, i => i.Label == "LogDirectory");
        Assert.Equal(Path.Combine(root, "Logs"), item.RecommendedValue);
    }

    [Fact]
    public void Detect_MultipleIssues_AllFlagged()
    {
        var root = MakeTempDir();
        var s = NewBareSettings();
        s.EnvsDir = "Envs";
        s.DefaultModelsDirectory = "Models";
        s.WorkflowsDirectory = "Workflow";

        var result = StartupPathProbe.Detect(s, root);
        Assert.Contains(result, i => i.Label == "EnvsDir");
        Assert.Contains(result, i => i.Label == "DefaultModelsDirectory");
        Assert.Contains(result, i => i.Label == "WorkflowsDirectory");
    }

    [Fact]
    public void Detect_AllFieldsValid_ReturnsEmpty()
    {
        var root = MakeTempDir();
        Directory.CreateDirectory(Path.Combine(root, "Python"));
        Directory.CreateDirectory(Path.Combine(root, "ENVTemplate"));
        Directory.CreateDirectory(Path.Combine(root, "Envs"));
        Directory.CreateDirectory(Path.Combine(root, "Nodes"));
        Directory.CreateDirectory(Path.Combine(root, "LocalNodes"));
        Directory.CreateDirectory(Path.Combine(root, "localnodes"));
        Directory.CreateDirectory(Path.Combine(root, "Models"));
        Directory.CreateDirectory(Path.Combine(root, "Workflow"));
        Directory.CreateDirectory(Path.Combine(root, "Logs"));

        var s = NewBareSettings();
        s.TemplatePythonDir = "Python";
        s.SystemTemplateLibraryDir = "ENVTemplate";
        s.EnvsDir = "Envs";
        s.GlobalNodesDir = "Nodes";
        s.LocalNodeDirectory = "LocalNodes";
        s.LocalNodesDirectory = "localnodes";
        s.DefaultModelsDirectory = "Models";
        s.WorkflowsDirectory = "Workflow";
        s.LogDirectory = "Logs";

        var result = StartupPathProbe.Detect(s, root);
        Assert.Empty(result);
    }
}