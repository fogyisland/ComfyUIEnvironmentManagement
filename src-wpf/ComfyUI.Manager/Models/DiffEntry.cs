namespace ComfyUI.Manager.Models;

public enum DiffCategory
{
    New,        // env 没装 → 装完会有
    Upgrade,    // env 装的比 spec.min 低 → 装完会升
    Downgrade,  // env 装的比 spec.max 高 → 装完会降
    Conflict,   // env 装的跟 spec 区间不重叠 → 装完会冲突
    NoChange,   // env 装的已满足 → 无变化
}

/// <summary>
/// 单条 pip 依赖变更。FromVersion = env 当前版本(null = 未装);ToVersion = spec 原文。
/// </summary>
public sealed record DiffEntry(string Name, DiffCategory Category, string? FromVersion, string? ToVersion)
{
    /// <summary>UI 显示用中文标签。</summary>
    public string CategoryLabel => Category switch
    {
        DiffCategory.New => "新增",
        DiffCategory.Upgrade => "升级",
        DiffCategory.Downgrade => "降级",
        DiffCategory.Conflict => "冲突",
        DiffCategory.NoChange => "无变化",
        _ => Category.ToString(),
    };
}