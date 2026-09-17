using System;
using ComfyUI.Manager.Data;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:把 T46 的 Settings.LocalModelsFilter 一次性迁到
/// model_settings.localmodels.search。
///
/// <para>
/// 启动时执行一次:读老 Settings.LocalModelsFilter,非空 → 写 model_settings
/// + 清老字段(避免后续双写)。全新用户(老 filter 为 null/空)= no-op。
/// </para>
///
/// <para>
/// 设计取舍:迁移一次后,SearchText setter 只写 model_settings,不再写老 Settings。
/// 但 Settings POCO 字段保留(标 [Obsolete] 注释),供 SettingsViewModel.SaveCommand
/// 序列化兼容(写入 null 无副作用)。
/// </para>
/// </summary>
public static class SearchFilterMigration
{
    /// <summary>v1.0.0.x (2026-09-17) T47:model_settings key for SearchText 持久化。</summary>
    public const string Key = "localmodels.search";

    /// <summary>
    /// 迁移:若老 filter 非空,写入 model_settings 并调用 <paramref name="clearOldFilter"/>
    /// 清空老字段。若老 filter 为 null/空,直接返回(全新用户,无需操作)。
    /// </summary>
    /// <param name="settings">model_settings DAO。</param>
    /// <param name="oldFilter">T46 的 Settings.LocalModelsFilter 当前值。</param>
    /// <param name="clearOldFilter">清空老字段的回调(VM 注入成 `v => Settings.LocalModelsFilter = v`)。</param>
    public static void Migrate(
        LocalModelSettingsRepository settings,
        string? oldFilter,
        Action<string?> clearOldFilter)
    {
        if (string.IsNullOrEmpty(oldFilter))
        {
            // 全新用户 — 老 filter 空,无数据可迁,不清字段(避免无谓写)。
            return;
        }

        // 老 T46 有数据:写到 model_settings + 清老字段
        settings.Set(Key, oldFilter);
        clearOldFilter(null);
    }
}