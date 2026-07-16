using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时把已经在 projectRoot 下的绝对路径迁移为相对路径,
/// 并给 template 类 path 字段填上 package root 下的默认子目录名。
///
/// 区分两类 path:
/// - **template paths** (TemplatePythonDir / TemplateComfyuiDir) — 默认填子目录名,
///   因为这些是 "包自带的资源" 类的东西(模板 Python / ComfyUI 源)应该落在程序根下,
///   不需要跑到额外的地方。
/// - **user-configured paths** (EnvsDir / GlobalNodesDir) — 默认保持空,
///   因为这些是用户主动管理的数据(env 列表 / 全局 catalog),不预创建不预填,
///   留到用户配置后再用。服务使用点(EnvCreatorService)在 path 为空时主动报错。
///
/// 存相对路径而非绝对路径:绿色 zip 跨机器/跨盘符时 settings.json 不需要
/// 重新生成。所有 path 使用方在运行时通过 Path.Combine(projectRoot, settings.X)
/// 解出绝对路径。
///
/// 默认子目录名:
///   - python    : 模板 Python 根(指向 package 自带的 portable python/ 目录,
///                 内含 3.10/3.11/.../python.exe)
///   - ComfyUI   : shared 布局的 ComfyUI 源(package root/ComfyUI/)
///   - envs      : EnvCreatorService 创建 env 时放这里(空 → 不预创建)
///   - global-nodes : 全局 catalog 节点根(空 → 不预创建)
/// </summary>
public static class SettingsDefaults
{
    public const string TemplatePythonSubdir = "python";
    public const string TemplateComfyuiSubdir = "ComfyUI";
    public const string EnvsSubdir = "envs";
    public const string GlobalNodesSubdir = "global-nodes";

    /// <summary>
    /// template paths 空时填默认值(子目录名);user-configured paths 空时保持空。
    /// 同时把已经在 projectRoot 下的绝对路径迁移为相对路径。
    ///
    /// 1. template paths 空 → 填默认子目录名(python / ComfyUI)
    /// 2. user-configured paths 空 → 保持空(交给服务层在使用时报错)
    /// 3. 相对路径 → 不动
    /// 4. 绝对路径且在 projectRoot 之下 → 转相对(剥掉 projectRoot 前缀)
    /// 5. 不在 projectRoot 下的绝对路径(用户故意选的别处)→ 保留
    /// </summary>
    public static void Apply(Settings s, string projectRoot)
    {
        if (s is null) return;

        s.TemplatePythonDir = Resolve(s.TemplatePythonDir, TemplatePythonSubdir, projectRoot);
        s.TemplateComfyuiDir = Resolve(s.TemplateComfyuiDir, TemplateComfyuiSubdir, projectRoot);
        s.EnvsDir = MigrateOnly(s.EnvsDir, projectRoot);
        s.GlobalNodesDir = MigrateOnly(s.GlobalNodesDir, projectRoot);
    }

    /// <summary>
    /// template path:空字段填默认子目录名,其余走迁移逻辑。
    /// </summary>
    private static string Resolve(string current, string defaultSubdir, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return defaultSubdir;
        }
        return MigrateOnly(current, projectRoot);
    }

    /// <summary>
    /// 空字符串 / 相对路径直接返回;绝对路径若在 projectRoot 下则转相对,
    /// 否则保留。
    /// </summary>
    private static string MigrateOnly(string current, string projectRoot)
    {
        // 空 → 保持空(交给服务层校验)
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
