namespace ComfyUI.Manager.Services;

/// <summary>
/// EnvCreatorService.CreateAsync 一次 env 创建过程中 emit 的进度事件。
///
/// Field 含义:
/// - Name:当前正在进行的 step 名称(CreateStepViewModel 用来匹配 + 切状态)
/// - Detail:当前 step 操作的具体路径/端口号等(给 UI 显示,不参与逻辑)
/// </summary>
public record CreateStepReport(string Name, string? Detail = null);
