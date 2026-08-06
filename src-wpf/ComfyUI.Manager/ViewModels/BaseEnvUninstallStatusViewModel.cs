using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// BaseEnvUninstallStatusViewModel:env-list 操作列"卸载基础环境"按钮的
/// inline 状态面板 VM(v0.6.5.22 T3)。
///
/// 跟 RequirementsStatusViewModel 同模式(observable collection + IsVisible +
/// Error + Hide),但只服务单次卸载流程:BaseEnvUninstaller 跑完后由调用方
/// (T4 EnvListViewModel.UninstallBaseEnvCommand) 调度 Complete() → 2s 延迟
/// → Hide()。
///
/// 行为:
/// - Begin() 后 IsVisible=true,LogLines 清空 + 追加"开始卸载基础环境..."
/// - AppendLog(line) 滚动 BaseEnvUninstaller 的 stdout/stderr
/// - Complete() 追加"卸载完成 — env 可重新部署基础环境"(不自动 Hide,由调用方延迟 2s 后 Hide)
/// - Fail(reason) 设 Error,IsVisible 保持,等用户手动关(由 UI 提供 ✕ 按钮 → CloseCommand)
/// - Hide() IsVisible=false
/// </summary>
public sealed class BaseEnvUninstallStatusViewModel : ViewModelBase
{
    public ObservableCollection<string> LogLines { get; } = new();

    public string? Error { get; private set; }

    public bool IsVisible { get; private set; }

    public RelayCommand CloseCommand { get; }

    public BaseEnvUninstallStatusViewModel()
    {
        CloseCommand = new RelayCommand(_ => Hide());
    }

    /// <summary>
    /// 开始一次卸载流程:清空状态 + 显示面板 + 追加起始日志行。
    /// </summary>
    public void Begin()
    {
        Error = null;
        LogLines.Clear();
        LogLines.Add("开始卸载基础环境...");
        IsVisible = true;
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>
    /// 追加一行日志(BaseEnvUninstaller stdout/stderr)。
    /// </summary>
    public void AppendLog(string line)
    {
        LogLines.Add(line);
    }

    /// <summary>
    /// 卸载完成:追加完成日志行。调用方负责延迟 2s 后调 Hide()(见 brief)。
    /// </summary>
    public void Complete()
    {
        LogLines.Add("卸载完成 — env 可重新部署基础环境");
    }

    /// <summary>
    /// 卸载失败:设 Error,IsVisible 保持,等用户手动关。
    /// 不追加日志行(跟 RequirementsStatusViewModel.Fail 一致)。
    /// </summary>
    public void Fail(string reason)
    {
        Error = reason;
        RaisePropertyChanged(nameof(Error));
    }

    /// <summary>
    /// 关闭面板:IsVisible=false。也由 CloseCommand(✕ 按钮)触发。
    /// </summary>
    public void Hide()
    {
        IsVisible = false;
        RaisePropertyChanged(nameof(IsVisible));
    }
}