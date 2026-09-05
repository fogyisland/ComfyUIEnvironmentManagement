namespace ComfyUI.Manager.ViewModels.FirstRunWizard;

// v1.0.0.x (2026-09-03) T34:扩展 enum 覆盖 8 个 settings 路径。
// 5 步分页:Welcome → Python(已有)→ Paths1EnvNodes(4 项)→
// Paths2ModelsWorkflows(2 项)→ Paths3SystemLog(2 项)→ Confirm。
//
// v1.0.0.x (2026-09-05):合并 Python + Git 为 Step 2 — 用户原话"在第二步骤写入 python /git
// 两个步骤 否则容易造成用户误解"。理由:Python + Git 都是 wizard 必填的解释器/工具路径,
// 用户心智模型是"安装 runtime" — 单一页面比 split 两页更直观。
// 6 步分页:Welcome → PythonGit(合并)→ Paths1EnvNodes → Paths2ModelsWorkflows →
// Paths3SystemLog → Confirm。
public enum FirstRunWizardStep
{
    Welcome,
    PythonGit,
    Paths1EnvNodes,
    Paths2ModelsWorkflows,
    Paths3SystemLog,
    Confirm,
}
