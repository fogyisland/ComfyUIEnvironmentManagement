namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

// v1.0.0.x (2026-09-03) T34:扩展 enum 覆盖 8 个 settings 路径。
// 5 步分页:Welcome → Python(已有)→ Paths1EnvNodes(4 项)→
// Paths2ModelsWorkflows(2 项)→ Paths3SystemLog(2 项)→ Confirm。
public enum FirstRunWizardStep
{
    Welcome,
    Python,
    Paths1EnvNodes,
    Paths2ModelsWorkflows,
    Paths3SystemLog,
    Confirm,
}
