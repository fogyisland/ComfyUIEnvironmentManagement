namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v1.0.0 Phase 1:开发模式标记 — DEBUG build 启用,RELEASE build 禁用。
///
/// 用途:
/// 1) App.OnStartup 跳过 FirstRun wizard(避免 dev 反复弹框),Settings 不被 wizard 覆盖
/// 2) SettingsDefaults.Apply 在 dev 把 HuggingFace/ModelScope 默认 enabled=true
///    (release 默认 disabled,用户主动勾选)
///
/// 实现用 <c>#if DEBUG</c> 让编译期常量固化 — 发布 build 里直接 const fold 成 false,
/// IL 不会带死分支,反编译也看不出"开发模式开关"存在。
/// </summary>
internal static class DevMode
{
#if DEBUG
    public const bool IsEnabled = true;
#else
    public const bool IsEnabled = false;
#endif
}