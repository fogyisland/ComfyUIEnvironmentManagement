using System.Globalization;
using System.Resources;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.9.3 final-review fix:MainWindow 顶层(顶部 menu + 齿轮按钮)用的本地化文案。
///
/// 跟 <see cref="DashboardPageLocalizable"/> 同款 ResourceManager 模式 —— csproj
/// 用 <c>MSBuild:_GenerateResxSource</c>,该 generator 只把 resx 编进二进制资源,
/// 不会生成 strong-typed Strings 类。所以这里走 ResourceManager 显式拿值。
///
/// 命名跟项目惯例对齐:<c>Menu_*</c> / <c>About_*</c> / <c>GearButton_*</c> —
/// 所有 MainWindow 顶层文案 key 集中在本类,便于 grep。
/// </summary>
public static class MainWindowLocalizable
{
    // csproj: <Resource Include="Resources\Strings*.resx"> 根命名空间 = ComfyUI.Manager
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(MainWindowLocalizable).Assembly);

    private static string Get(string key, string fallback) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;

    /// <summary>v0.6.9.3 final-review fix:齿轮按钮 tooltip — 之前硬编码 "设置"。</summary>
    public static string GearButtonTooltip => Get("GearButton_Tooltip", "设置");
}