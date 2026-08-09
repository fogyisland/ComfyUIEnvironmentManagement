using System.Globalization;
using System.Resources;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.9.3 T1:集中提供主导航分区的本地化显示名，供状态栏等入口复用。
/// </summary>
public static class MainSectionNameProvider
{
    private static readonly ResourceManager StringsResources = new(
        "ComfyUI.Manager.Resources.Strings",
        typeof(MainSectionNameProvider).Assembly);

    public static string GetName(MainSection section) => section switch
    {
        MainSection.Dashboard => Get("SectionName_Dashboard", "主页"),
        MainSection.Environments => Get("SectionName_Environments", "环境"),
        MainSection.Catalog => Get("SectionName_Catalog", "节点目录"),
        MainSection.BaseEnv => Get("SectionName_BaseEnv", "基础环境"),
        MainSection.Settings => Get("SectionName_Settings", "设置"),
        MainSection.BulkUpdate => Get("SectionName_BulkUpdate", "批量更新"),
        MainSection.SystemStatus => Get("SectionName_SystemStatus", "系统状态"),
        _ => section.ToString()
    };

    private static string Get(string key, string fallback) =>
        StringsResources.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;
}
