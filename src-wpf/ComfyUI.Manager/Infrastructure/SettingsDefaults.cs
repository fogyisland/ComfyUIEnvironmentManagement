using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时给 path 类字段填默认(程序目录下的子文件夹)。
///
/// 只填空字段,不覆盖已有值。调用方决定是否 Save —— 通常 App.xaml.cs 在
/// Load 之后立即调用 + Save,之后 settings.json 自带绝对路径。
///
/// 默认子文件夹名是常量,方便测试和文档引用:
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

    public static void Apply(Settings s, string projectRoot)
    {
        if (s is null) return;
        if (string.IsNullOrWhiteSpace(s.TemplatePythonDir))
        {
            s.TemplatePythonDir = Path.Combine(projectRoot, TemplatePythonSubdir);
        }
        if (string.IsNullOrWhiteSpace(s.TemplateComfyuiDir))
        {
            s.TemplateComfyuiDir = Path.Combine(projectRoot, TemplateComfyuiSubdir);
        }
        if (string.IsNullOrWhiteSpace(s.EnvsDir))
        {
            s.EnvsDir = Path.Combine(projectRoot, EnvsSubdir);
        }
        if (string.IsNullOrWhiteSpace(s.GlobalNodesDir))
        {
            s.GlobalNodesDir = Path.Combine(projectRoot, GlobalNodesSubdir);
        }
    }
}