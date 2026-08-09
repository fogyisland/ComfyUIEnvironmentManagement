using System;
using System.Windows;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.9 T1:UI Modernization 主题模式。
/// <see cref="Light"/> / <see cref="Dark"/> 显式;<see cref="FollowSystem"/>
/// 启动时按系统当前主题解析一次到 Light/Dark(简化 heuristic,见
/// <see cref="ThemeService.ResolveMode"/> 注释;后续 T2 换 SystemTheme API)。
/// </summary>
public enum ThemeMode
{
    Light,
    Dark,
    FollowSystem
}

/// <summary>
/// 主题切换服务。T1 职责:原子替换 Application.Resources.MergedDictionaries
/// 中的 palette 槽位(Themes/Palette.Light.xaml 或 Themes/Palette.Dark.xaml)。
/// T2 接 Settings UI 后调 <see cref="Apply"/> 实现运行时切换;T3+ 视图迁移
/// StaticResource → DynamicResource 后才能 Live 看效果。
/// </summary>
public interface IThemeService
{
    /// <summary>当前已 apply 的主题模式(FollowSystem 启动后落定到 Light/Dark)。</summary>
    ThemeMode Current { get; }

    /// <summary>应用指定主题;若 <paramref name="mode"/> 无效或加载失败,fallback Dark。</summary>
    void Apply(ThemeMode mode);

    /// <summary>
    /// v0.6.9 T9:Apply 之前触发(仅当 mode 解析后跟 Current 不同,避免无意义的 cross-fade)。
    /// 给 MainWindow 接 cross-fade overlay 用。Payload = 最终生效的 mode(已 ResolveMode)。
    /// </summary>
    event EventHandler<ThemeMode>? ThemeChanging;

    /// <summary>Apply 完成后触发,payload = 最终生效的 mode(已 ResolveMode)。</summary>
    event EventHandler<ThemeMode>? Applied;
}

/// <summary>
/// 默认实现。ctor 接 ResourceDictionary(测试方便 + 生产路径 T2 给
/// <c>Application.Current.Resources</c>);ctor 不抛 — 即使 palette dict 缺失
/// 也延迟到第一次 Apply 才报错。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string LightPalettePath = "/ComfyUI.Manager;component/Themes/Palette.Light.xaml";
    private const string DarkPalettePath = "/ComfyUI.Manager;component/Themes/Palette.Dark.xaml";

    private readonly ResourceDictionary _appResources;
    private readonly AppLogger? _logger;

    public ThemeService(ResourceDictionary appResources, AppLogger? logger = null)
    {
        _appResources = appResources;
        _logger = logger;
    }

    public ThemeMode Current { get; private set; } = ThemeMode.Dark;

    /// <inheritdoc />
    public event EventHandler<ThemeMode>? ThemeChanging;

    public event EventHandler<ThemeMode>? Applied;

    public void Apply(ThemeMode mode)
    {
        var resolved = ResolveMode(mode);

        // v0.6.9 T9:Apply 前 broadcast ThemeChanging(给 MainWindow 触 cross-fade)。
        // 跳过同 mode(避免首次启动无意义 fade) — 但必须在 ReplacePaletteSlot 之前发,
        // 这样 listener 可以 fade-out → 看 mode swap → fade-in。
        if (resolved != Current)
        {
            ThemeChanging?.Invoke(this, resolved);
        }

        try
        {
            // 原子替换:删所有 palette dicts,加新的。
            // 注意:Theme.xaml 在更早位置合并,Clear() 删所有后再重新加 theme + palette 槽位
            // 才能保证 palette slot 总存在 → 资源查找 PrimaryBrush 命中 palette。
            // 但 T1 不删 Theme.xaml 槽位 — 只删 theme.xaml *后面* 的 palette 槽。
            // 实现更稳的方式:保留 theme dict,把 palette slot 单独管理。T2 会重构成
            // 按槽位管理。当前 Clear + Add 1 个 palette = 把所有 merged dict 都清掉再
            // 只加 palette 是不行的(会丢 Theme.xaml) → 改用 replace-by-uri:
            // 找到现有的 palette dict 替换它,没有就 append。
            ReplacePaletteSlot(resolved);

            Current = resolved;
            Applied?.Invoke(this, resolved);
        }
        catch (Exception ex)
        {
            _logger?.Error("theme", $"Apply({mode}) failed: {ex.Message}");
            // fallback Dark
            Current = ThemeMode.Dark;
        }
    }

    private void ReplacePaletteSlot(ThemeMode resolved)
    {
        var paletteUri = resolved == ThemeMode.Light
            ? new Uri(LightPalettePath, UriKind.Relative)
            : new Uri(DarkPalettePath, UriKind.Relative);

        // 找现有的 palette dict(slot by App.xaml x:Name 不直接可访问 — 走 Source 匹配)
        ResourceDictionary? existing = null;
        for (int i = 0; i < _appResources.MergedDictionaries.Count; i++)
        {
            var md = _appResources.MergedDictionaries[i];
            if (md.Source is { } src &&
                (src.ToString().EndsWith("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                 src.ToString().EndsWith("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                existing = md;
                break;
            }
        }

        if (existing is not null)
        {
            // 同 resolved 直接 no-op(已经加载过),不同则替换 Source(强制重新加载)
            if (existing.Source is { } exSrc && exSrc.ToString().EndsWith(
                    (resolved == ThemeMode.Light ? "Light" : "Dark") + ".xaml",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _appResources.MergedDictionaries.Remove(existing);
        }

        _appResources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = paletteUri
        });
    }

    /// <summary>
    /// 把 ThemeMode 落定到 Light/Dark。FollowSystem 用 WPF
    /// <see cref="SystemParameters.WindowGlassBrush"/> 作简化 heuristic
    /// (Win10+ 才支持 glass,旧 Windows / Server 返回 null → fallback Light);
    /// 真 system theme 检测走 Win32 <c>GetSystemColor(COLOR_WINDOW)</c>,
    /// out-of-scope,T2 接 Settings 时升级。失败统一 fallback Dark。
    /// </summary>
    private static ThemeMode ResolveMode(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ThemeMode.Light,
        ThemeMode.Dark => ThemeMode.Dark,
        ThemeMode.FollowSystem => SystemParameters.WindowGlassBrush != null
            ? ThemeMode.Dark
            : ThemeMode.Light,
        _ => ThemeMode.Dark
    };
}
