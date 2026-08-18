using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ComfyUI.Manager.ViewModels;

public sealed class EnvStartStatusViewModel : ViewModelBase, IProgress<string>
{
    private static readonly string[] StageNames = { "激活本地环境", "在环境中启用", "完成" };

    public IReadOnlyList<string> Stages { get; } = StageNames;
    public int CurrentStageIndex { get; private set; } = -1;
    public string CurrentStageText => CurrentStageIndex >= 0 ? StageNames[CurrentStageIndex] : "";
    public ObservableCollection<string> LogLines { get; } = new();
    public string? Error { get; private set; }
    public bool IsVisible { get; private set; }
    public bool IsComplete => CurrentStageIndex == StageNames.Length - 1;

    /// <summary>
    /// v0.6.17:面板标题 — ctor 设默认 "启动状态",生产代码(StartEnvAsync /
    /// RestartEnvInternalAsync)改成 "启动状态 — {env.Name}" 让多 env 切换时区分。
    /// </summary>
    public string Title { get; set; } = "启动状态";

    public void Begin()
    {
        CurrentStageIndex = 0;
        IsVisible = true;
        RaisePropertyChanged(nameof(CurrentStageIndex));
        RaisePropertyChanged(nameof(CurrentStageText));
        RaisePropertyChanged(nameof(IsVisible));
    }

    public void AdvanceTo(string stageName)
    {
        var idx = Array.IndexOf(StageNames, stageName);
        if (idx < 0) return;
        CurrentStageIndex = idx;
        RaisePropertyChanged(nameof(CurrentStageIndex));
        RaisePropertyChanged(nameof(CurrentStageText));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Complete()
    {
        AdvanceTo("完成");
    }

    public void Fail(string reason)
    {
        Error = reason;
        RaisePropertyChanged(nameof(Error));
    }

    public void Hide()
    {
        IsVisible = false;
        CurrentStageIndex = -1;
        Error = null;
        LogLines.Clear();
        RaisePropertyChanged(nameof(IsVisible));
        RaisePropertyChanged(nameof(CurrentStageIndex));
        RaisePropertyChanged(nameof(CurrentStageText));
        RaisePropertyChanged(nameof(Error));
        RaisePropertyChanged(nameof(IsComplete));
    }

    public void Report(string value)
    {
        if (value != null && value.StartsWith("stage:"))
        {
            AdvanceTo(value.Substring("stage:".Length));
        }
        else
        {
            LogLines.Add(value ?? string.Empty);
        }
    }
}
