namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.14 picker redesign:filter chip 数据源项。
///
/// <para>
/// ItemsControl 模板下 RadioButton 的 <c>ConverterParameter</c> 不能直接
/// 绑 <c>{Binding}</c>(WPF markup 评估返 Binding 实例 ≠ enum 值 — Critical 1)。
/// 也写不了字面串(每条数据驱动)。改方案:用这个 wrapper 同时携带
/// <see cref="Filter"/>(enum,供 click handler + 比较)、
/// <see cref="FilterName"/>(enum-name 字符串,供 EnumEqualsConverter 解析)和
/// <see cref="Label"/>(中文显示文案,原 PickerFilterToLabelConverter 的职责)。
/// </para>
///
/// <para>
/// XAML 用法:
/// <code>
/// IsChecked="{Binding DataContext.ActiveFilter, ...
///            Converter={StaticResource EnumEqualsConverter},
///            ConverterParameter={Binding FilterName}}"
/// Content="{Binding Label}"
/// </code>
/// </para>
/// </summary>
public sealed class PickerFilterOption
{
    public PickerFilter Filter { get; }
    public string FilterName { get; }
    public string Label { get; }

    public PickerFilterOption(PickerFilter filter, string filterName, string label)
    {
        Filter = filter;
        FilterName = filterName;
        Label = label;
    }
}
