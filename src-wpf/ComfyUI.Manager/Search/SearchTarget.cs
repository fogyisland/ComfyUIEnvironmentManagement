namespace ComfyUI.Manager.Search;

/// <summary>
/// 搜索目标种类。T7 导航分发按 kind 决定调用方:
///   - Environment:MainViewModel.ShowEnvironments + 选中 + scroll
///   - Node:同上,再 drill-in 到 node
///   - SettingsSection:SettingsView 内部 scroll 到 sectionKey
///   - Command:触发 MainViewModel.{CommandName}Command
/// </summary>
public enum TargetKind
{
    Environment,
    Node,
    SettingsSection,
    Command,
}

/// <summary>
/// 描述"用户搜索到这条结果后,按 enter 应该导航去哪里"。
/// 每个 kind 只设自己的必填字段,其它字段保持 null(便于 assertion / 测试)。
/// </summary>
public sealed record SearchTarget(
    TargetKind Kind,
    string? EnvId = null,
    string? NodeId = null,
    string? CommandName = null,
    string? SectionKey = null,
    string DisplayName = "")
{
    /// <summary>便利工厂:Environment target。</summary>
    public static SearchTarget ForEnvironment(string envId, string displayName) =>
        new(TargetKind.Environment, EnvId: envId, DisplayName: displayName);

    /// <summary>便利工厂:Node target。</summary>
    public static SearchTarget ForNode(string envId, string nodeId, string displayName) =>
        new(TargetKind.Node, EnvId: envId, NodeId: nodeId, DisplayName: displayName);

    /// <summary>便利工厂:Settings section target。</summary>
    public static SearchTarget ForSettingsSection(string sectionKey, string displayName) =>
        new(TargetKind.SettingsSection, SectionKey: sectionKey, DisplayName: displayName);

    /// <summary>便利工厂:Command target。CommandName 必须跟 MainViewModel property 同名(如 "ShowDashboard")。</summary>
    public static SearchTarget ForCommand(string commandName, string displayName) =>
        new(TargetKind.Command, CommandName: commandName, DisplayName: displayName);
}
