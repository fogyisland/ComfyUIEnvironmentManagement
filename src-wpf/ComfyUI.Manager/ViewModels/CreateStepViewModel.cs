using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// CreateEnvDialog 步骤进度条目:每个 step 在 ViewModel 里一个实例,
/// 把 CreateStepReport 投影成可绑定的属性(Name / Detail / Status / Glyph)。
/// 配合 WPF DataTemplate 的 Status → color/glyph DataTrigger 把
/// "○ 待办 / ● 进行中 / ✓ 完成 / ✗ 失败" 渲染出来。
/// </summary>
public class CreateStepViewModel : ViewModelBase
{
    public string Name { get; }

    private string? _detail;
    public string? Detail
    {
        get => _detail;
        set => SetField(ref _detail, value);
    }

    private CreateStepStatus _status = CreateStepStatus.Pending;
    public CreateStepStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                RaisePropertyChanged(nameof(Glyph));
            }
        }
    }

    public string Glyph => _status switch
    {
        CreateStepStatus.Pending => "○",
        CreateStepStatus.Running => "●",
        CreateStepStatus.Done => "✓",
        CreateStepStatus.Failed => "✗",
        _ => "?",
    };

    public CreateStepViewModel(string name)
    {
        Name = name;
    }
}
