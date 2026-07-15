using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时把已经在 projectRoot 下的绝对路径迁移为相对路径。
///
/// 不再自动填充默认值:用户没填的 path 字段保持空,等用户明确配置后再用。
/// 服务使用点(EnvCreatorService 等)在 path 为空时主动报错,
/// 告诉用户去设置页填。
///
/// 存相对路径而非绝对路径:绿色 zip 跨机器/跨盘符时 settings.json 不需要
/// 重新生成。所有 path 使用方在运行时通过 Path.Combine(projectRoot, settings.X)
/// 解出绝对路径。
///
/// 默认子目录名常量保留,作为设置页 placeholder / 文档参考用,
/// 但 Apply 不会再把它们写进 settings.json:
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
    /// 只做绝对路径 → 相对路径迁移,不填充默认值:
    /// 1. 空字段 → 保持空(交给服务层在使用时报错)
    /// 2. 相对路径 → 不动
    /// 3. 绝对路径且在 projectRoot 之下 → 转相对(剥掉 projectRoot 前缀)
    /// 4. 不在 projectRoot 下的绝对路径(用户故意选的别处)→ 保留
    /// </summary>
    public static void Apply(Settings s, string projectRoot)
    {
        if (s is null) return;

        s.TemplatePythonDir = MigrateToRelative(s.TemplatePythonDir, projectRoot);
        s.TemplateComfyuiDir = MigrateToRelative(s.TemplateComfyuiDir, projectRoot);
        s.EnvsDir = MigrateToRelative(s.EnvsDir, projectRoot);
        s.GlobalNodesDir = MigrateToRelative(s.GlobalNodesDir, projectRoot);
    }

    /// <summary>
    /// 空字符串 / 相对路径直接返回;绝对路径若在 projectRoot 下则转相对,
    /// 否则保留。
    /// </summary>
    private static string MigrateToRelative(string current, string projectRoot)
    {
        // 空 → 保持空(交给服务层校验,不要凭空填默认值)
        if (string.IsNullOrWhiteSpace(current))
        {
            return current;
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
