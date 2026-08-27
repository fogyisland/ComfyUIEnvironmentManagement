namespace ComfyUI.Manager.Models;

/// <summary>
/// v1.0.0.x #594:启动期路径错位检测输出项 — 承载可疑路径的 label + 当前值 + 推荐新值。
///
/// 由 <see cref="ComfyUI.Manager.Services.StartupPathProbe"/> 生成,被
/// <see cref="ComfyUI.Manager.ViewModels.PathMigrationConfirmViewModel"/> 包装成 UI 项。
///
/// 注意:这里的 <see cref="CurrentValue"/> 是持久化在 settings.inf / settings.json 里
/// 的原始值(可能是老盘符上的绝对路径或相对子目录名);<see cref="RecommendedValue"/>
/// 是当前 projectRoot + 默认 subdir 拼出的绝对路径。
/// </summary>
public sealed record PathMigrationItem(
    string Label,
    string CurrentValue,
    string RecommendedValue);