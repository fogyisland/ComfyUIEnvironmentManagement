using System;
using System.IO;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.11 T4:Catalog 样式 polish — 静态 grep Theme.xaml 的
/// <c>CatalogTileTemplate</c> 区段,断言不存在硬编码颜色字面值。
///
/// 缘由:之前 tile 的 Background="White" / BorderBrush="LightGray" /
/// Foreground="Gray" / hover BorderBrush="#90A4AE" 都不跟随主题切换,
/// 暗主题下仍是亮色背景。本 task 把它们全部换成 {DynamicResource XxxBrush}。
/// 此测试守住这条线:未来如果有人手滑加回硬编码颜色,build 就能看到失败。
///
/// 实现策略:不解析 XAML 结构,直接 text match。这避免引入 XAML DOM 依赖,
/// 也避开 property-element / attribute / markup extension 解析差异。
/// slice 只看 <c>CatalogTileTemplate</c> 那个 DataTemplate 区段内的内容,
/// 其他 Style / Trigger 不参与本测试(它们各自有自己的硬编码颜色规则)。
/// </summary>
public class CatalogStyleTests
{
    private const string ThemeRelativePath = "src-wpf/ComfyUI.Manager/Resources/Theme.xaml";

    [Fact]
    public void Theme_xaml_CatalogTile_NoHardcodedColorValues()
    {
        var themePath = ResolveRepoRelativePath(ThemeRelativePath);
        Assert.True(File.Exists(themePath),
            $"Theme.xaml not found at '{themePath}'. 测试要求文件在 repo root 的 " +
            $"{ThemeRelativePath}。");

        var content = File.ReadAllText(themePath);
        var tileSlice = ExtractCatalogTileTemplateSlice(content);
        Assert.False(string.IsNullOrEmpty(tileSlice),
            "未找到 CatalogTileTemplate 区段 — 模板名变了?如果是改名,本测试也要改。");

        // Background / BorderBrush / Foreground 直接命中常见硬编码颜色。
        Assert.DoesNotContain("White", tileSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("LightGray", tileSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("Gray", tileSlice, StringComparison.Ordinal);

        // hover 边框之前用的灰蓝色 hex,现在改 DynamicResource PrimaryVariantBrush。
        Assert.DoesNotContain("#90A4AE", tileSlice, StringComparison.Ordinal);
    }

    /// <summary>
    /// 在 Theme.xaml 内容里取 <c>&lt;DataTemplate x:Key="CatalogTileTemplate"&gt;</c>
    /// 到下一个 <c>&lt;/DataTemplate&gt;</c> 之间的内容(包括起始 DataTemplate 标签)。
    /// 如果改名为别的 key,这里也同步改。
    /// </summary>
    private static string ExtractCatalogTileTemplateSlice(string content)
    {
        const string startMarker = "x:Key=\"CatalogTileTemplate\"";
        const string endMarker = "</DataTemplate>";

        var startIdx = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0) return string.Empty;

        // 从 startMarker 往前找 "<DataTemplate" 起始标签,确保 slice 包含整段。
        var dtStart = content.LastIndexOf("<DataTemplate", startIdx, StringComparison.Ordinal);
        if (dtStart < 0) return string.Empty;

        var endIdx = content.IndexOf(endMarker, dtStart, StringComparison.Ordinal);
        if (endIdx < 0) return string.Empty;

        // 包含 closing 标签。
        return content.Substring(dtStart, endIdx - dtStart + endMarker.Length);
    }

    /// <summary>
    /// 测试运行时 cwd 不一定是 repo root — 用 .csproj 探针往上找,
    /// 一直找到含 <c>src-wpf/</c> 的目录作为 repo root。
    /// </summary>
    private static string ResolveRepoRelativePath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src-wpf")))
            {
                return Path.Combine(dir, relativePath);
            }
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        // Fallback:用相对路径当绝对路径(开发机 cwd 通常就是 repo root)。
        return relativePath;
    }
}