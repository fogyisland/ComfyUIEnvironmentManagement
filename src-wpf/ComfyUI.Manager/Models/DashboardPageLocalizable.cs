using System.Globalization;
using System.Resources;

namespace ComfyUI.Manager.Models;

/// <summary>
/// v0.6.9 T5:Dashboard 页面的本地化文案集中点。
///
/// 跟 <see cref="AboutDialogViewModel"/> / <see cref="DonateQrViewModel"/> 一致 ——
/// csproj 用 <c>MSBuild:_GenerateResxSource</c>,该 generator 只把 resx 编进
/// 二进制资源,不会生成 strong-typed Strings 类。所以这里走 ResourceManager
/// 显式拿值:key 在 <c>Resources\Strings.resx</c> 定义,fallback 到内联常量
/// (resx 缺 key 或 build 失败时不至于 NRE)。
///
/// 命名跟项目惯例对齐:<c>CatalogPage_*</c> / <c>SettingsPage_*</c> —
/// 所有 key 以 <c>DashboardPage_</c> 开头,便于 grep。
/// </summary>
public static class DashboardPageLocalizable
{
    // csproj: <Resource Include="Resources\Strings*.resx"> 根命名空间 = ComfyUI.Manager
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(DashboardPageLocalizable).Assembly);

    private static string Get(string key, string fallback) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;

    public static string Title => Get("DashboardPage_欢迎面板", "欢迎面板");
    public static string Refresh => Get("DashboardPage_刷新", "刷新");
    public static string Loading => Get("DashboardPage_加载中", "加载中...");
    public static string EnvironmentStats => Get("DashboardPage_环境统计", "环境统计");
    public static string Running => Get("DashboardPage_运行中", "运行");
    public static string Stopped => Get("DashboardPage_已停止", "停止");
    public static string Undeployed => Get("DashboardPage_未部署", "未装");
    public static string Total => Get("DashboardPage_总数", "总数");
    public static string NodeCount => Get("DashboardPage_节点总数", "节点总数");
    public static string RecentOps => Get("DashboardPage_最近操作", "最近操作");
    public static string LatestVersion => Get("DashboardPage_最新版本", "最新版本");
    public static string GitHubFailed => Get("DashboardPage_网络异常", "⚠ 网络异常");
}