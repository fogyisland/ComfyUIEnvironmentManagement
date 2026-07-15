using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时给 path 类字段填默认(子目录名,相对路径),
/// 并把已经在 projectRoot 下的绝对路径迁移为相对路径。
///
/// 存相对路径而非绝对路径:绿色 zip 跨机器/跨盘符时 settings.json 不需要
/// 重新生成。所有 path 使用方(EnvCreatorService 等)在运行时通过
/// Path.Combine(projectRoot, settings.X) 解出绝对路径。
///
/// 默认子目录名是常量,方便测试和文档引用:
///   - template-python : 模板 Python 根(内含 3.10/3.11/.../python.exe)
///   - ComfyUI         : shared 布局的 ComfyUI 源
///   - envs            : EnvCreatorService 创建 env 时放这里
///   - global-nodes    : 全局 catalog 节点根
/// </summary>
public static class SettingsDefaults
{
    public const string TemplatePythonSubdir = "template-python";
    public const string TemplateComfyuiSubdir = "ComfyUI";
    public const string EnvsSubdir = "envs";
    public const string GlobalNodesSubdir = "global-nodes";

    /// <summary>
    /// 填空 + 迁移绝对路径 → 相对子路径。
    /// 1. 空字段 → 默认子目录名
    /// 2. 绝对路径且在 projectRoot 之下 → 转相对(剥掉 projectRoot 前缀)
    /// 3. 已经是相对的 / 不在 projectRoot 下的 → 不动
    /// </summary>
    public static void Apply(Settings s, string projectRoot)
    {
        if (s is null) return;

        s.TemplatePythonDir = Resolve(s.TemplatePythonDir, TemplatePythonSubdir, projectRoot);
        s.TemplateComfyuiDir = Resolve(s.TemplateComfyuiDir, TemplateComfyuiSubdir, projectRoot);
        s.EnvsDir = Resolve(s.EnvsDir, EnvsSubdir, projectRoot);
        s.GlobalNodesDir = Resolve(s.GlobalNodesDir, GlobalNodesSubdir, projectRoot);
    }

    private static string Resolve(string current, string defaultSubdir, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return defaultSubdir;
        }
        // 已经是相对的 → 不动
        if (!Path.IsPathRooted(current))
        {
            return current;
        }
        // 绝对路径:尝试剥 projectRoot 前缀,成功就转相对
        try
        {
            var fullProject = Path.GetFullPath(projectRoot);
            var fullCurrent = Path.GetFullPath(current);
            var fullProjectWithSep = fullProject.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullCurrent.StartsWith(fullProjectWithSep, System.StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullCurrent.Substring(fullProjectWithSep.Length);
                // 用 Path.GetRelativePath 规范化分隔符,避免漏 ..\ 或混 \ 和 /
                return Path.GetRelativePath(fullProject, fullCurrent);
            }
        }
        catch
        {
            // Path.GetFullPath 失败(罕见)→ 当作不可迁移,保留原值
        }
        // 不在 projectRoot 下的绝对路径(用户故意选的别处)→ 保留
        return current;
    }
}