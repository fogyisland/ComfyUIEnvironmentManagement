using System;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v0.6.11+ dashboard/splash polish:4-stage 加权进度报告器的 stage 枚举。
/// Splash 用:Init(25%) → LoadDatabase(25%) → LoadTheme(25%) → Ready(25%)。
/// </summary>
public enum Stage
{
    Init = 0,
    LoadDatabase = 1,
    LoadTheme = 2,
    Ready = 3,
}

/// <summary>
/// v0.6.11+ dashboard/splash polish:4-stage 加权进度报告器。
/// 每次 Report 给定 stage + stage 内 percent(0-100);TotalPercent = sum of (stage weight × stage pct / 100)。
/// UI thread 用;后台 thread 调需要 Dispatcher.Invoke 包一层。
/// </summary>
public sealed class MultiStageSplashProgress
{
    /// <summary>每 stage 占总进度 25%,共 4 stage = 100%。</summary>
    private static readonly int[] StageWeights = { 25, 25, 25, 25 };

    private readonly int[] _stagePercents = new int[4];

    public int TotalPercent
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _stagePercents.Length; i++)
                total += StageWeights[i] * _stagePercents[i] / 100;
            return Math.Clamp(total, 0, 100);
        }
    }

    public void Report(Stage stage, int stagePercent)
    {
        var idx = (int)stage;
        var clamped = Math.Clamp(stagePercent, 0, 100);
        _stagePercents[idx] = clamped;
        StageChanged?.Invoke(stage, clamped);
    }

    public event Action<Stage, int>? StageChanged;
}
