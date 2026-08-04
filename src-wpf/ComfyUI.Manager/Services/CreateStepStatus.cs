namespace ComfyUI.Manager.Services;

/// <summary>
/// CreateEnvDialog 步骤状态:UI 进度面板用的 enum。
///
/// Pending:还没开始(默认样式)
/// Running:当前正在执行(高亮 + 加粗)
/// Done:成功完成(绿色 + 勾)
/// Failed:失败(红色 + 叉)
/// </summary>
public enum CreateStepStatus
{
    Pending,
    Running,
    Done,
    Failed,
}
