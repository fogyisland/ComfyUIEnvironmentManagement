using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// Environment:row of the <c>environments</c> table.
/// </summary>
public class Environment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("root_path")]
    public string RootPath { get; set; } = "";
    [JsonPropertyName("comfyui_layout")]
    public string ComfyuiLayout { get; set; } = "isolated";
    [JsonPropertyName("comfyui_source")]
    public string? ComfyuiSource { get; set; }
    [JsonPropertyName("venv_path")]
    public string? VenvPath { get; set; }
    [JsonPropertyName("python_executable")]
    public string? PythonExecutable { get; set; }
    [JsonPropertyName("custom_nodes_path")]
    public string? CustomNodesPath { get; set; }
    [JsonPropertyName("extra_model_paths_yaml")]
    public string? ExtraModelPathsYaml { get; set; }
    [JsonPropertyName("port")]
    public int? Port { get; set; }
    [JsonPropertyName("enabled_node_ids_json")]
    public string EnabledNodeIdsJson { get; set; } = "[]";
    [JsonPropertyName("status")]
    public string Status { get; set; } = "stopped";
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }
    [JsonPropertyName("base_python_path")]
    public string BasePythonPath { get; set; } = "";
    [JsonPropertyName("python_version")]
    public string PythonVersion { get; set; } = "";
    [JsonPropertyName("bed_profile_id")]
    public string? BedProfileId { get; set; }
    [JsonPropertyName("bed_status")]
    public string? BedStatus { get; set; }
    [JsonPropertyName("bed_failed_reason")]
    public string? BedFailedReason { get; set; }
    /// <summary>
    /// v0.6.7.2:用户备注(例如"测试 SDXL 工作流"、"用 ControlNet 验证节点")。
    /// 新建环境时在 CreateEnvDialog 输入,后续编辑留给 UI 接。
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// v1.0.0 multi-template T3:此 env 创建时使用的 template kind(对应
    /// <see cref="Settings.Templates"/> 的 key,例如 "ComfyUI" / "A1111")。
    /// 持久化到 SQLite <c>environments.template_kind</c> 列;老行 backfill 到 "ComfyUI"。
    /// </summary>
    [JsonPropertyName("template_kind")]
    public string TemplateKind { get; set; } = "ComfyUI";

    /// <summary>
    /// v1.0.0 multi-template T3:env 创建时 <see cref="TemplateConfig"/> 的快照。
    /// JSON 序列化为 <c>environments.template_config_snapshot</c> 列;老行(无该列)backfill
    /// 到当前 <see cref="Settings.Templates"/>["ComfyUI"] 的拷贝。快照设计保证
    /// <c>Settings.Templates</c> 后续修改不会影响既有 env 的可重现性。
    /// </summary>
    [JsonPropertyName("template_config_snapshot")]
    public TemplateConfig? TemplateConfigSnapshot { get; set; }

    /// <summary>
    /// v0.6.11+ T3:env-list 行 toggle 按钮用 — true = ComfyUI Manager 已装(显示"卸载"),
    /// false = 未装(显示"安装")。每次 Load 末尾重新算,不持久化(避免 stale)。
    /// EnvironmentRepository 用 System.Text.Json 序列化,JsonIgnore 防止 SQLite 写入。
    /// </summary>
    [JsonIgnore]
    public bool IsComfyUiManagerInstalled { get; set; }

    /// <summary>
    /// v0.6.11+ T3:toggle 按钮文字,根据 IsComfyUiManagerInstalled 切换。
    /// </summary>
    [JsonIgnore]
    public string ComfyUiManagerButtonText { get; set; } = "安装 ComfyUI Manager";

    /// <summary>
    /// v1.0.0.x:A1111 / Forge / SwarmUI env 行不显示「安装 ComfyUI Manager」按钮 —
    /// ComfyUI-Manager 是 ComfyUI 专属 custom_nodes extension,SD Web 用 extensions 体系,
    /// 没有 ComfyUI-Manager 概念。「装依赖」按钮保留(sdweb 也有非 torch 依赖要装)。
    /// </summary>
    [JsonIgnore]
    public bool ComfyUiManagerButtonVisible => TemplateKind == "ComfyUI";

    /// <summary>
    /// v1.0.0.x #577:env-list 行 toggle 按钮用 — true = 本地常用节点已全部装好
    /// (Settings.LocalNodesDirectory 下每个子包都已在 env/custom_nodes/),false = 未装或不全。
    /// Load 末尾重新算(LocalNodeInstaller.IsInstalled),不持久化(同 IsComfyUiManagerInstalled pattern)。
    /// </summary>
    [JsonIgnore]
    public bool IsLocalNodesInstalled { get; set; }

    /// <summary>
    /// v1.0.0.x #577:toggle 按钮文字,根据 IsLocalNodesInstalled 切换。
    /// </summary>
    [JsonIgnore]
    public string LocalNodesButtonText { get; set; } = "安装本地常用";

    /// <summary>
    /// v1.0.0.x:A1111 / Forge / SwarmUI env 行不显示「安装本地常用」按钮 ——
    /// 「本地常用」是用户预下载的 ComfyUI custom_nodes 包(逐个 copy 到 env/custom_nodes/),
    /// A1111 / Forge 用 extensions/ 体系,SwarmUI 用自己的 Modules 目录,跟 ComfyUI
    /// custom_nodes 不兼容。镜像 <see cref="ComfyUiManagerButtonVisible"/> 同模式。
    /// </summary>
    [JsonIgnore]
    public bool LocalNodesButtonVisible => TemplateKind == "ComfyUI";

    /// <summary>
    /// v1.0.0.x:A1111 / Forge / SwarmUI env 行不显示「节点管理」按钮 ——
    /// 节点管理打开的是 <c>NodeManagementView</c>(节点扫描 / 安装 / 升级 / 卸载),
    /// 底层操作的是 env 的 custom_nodes/ 目录 + ScannedNode DB。A1111 / Forge 用
    /// extensions/ 体系,SwarmUI 用 Modules 目录,跟 ComfyUI custom_nodes 不兼容,
    /// 节点管理无意义。镜像 <see cref="LocalNodesButtonVisible"/> 同模式。
    /// 注:<see cref="ViewModels.EnvironmentListViewModel.OpenNodeManagementCommand"/>
    /// 不挡 TemplateKind(只在 VM 隐藏 — 通过 XAML Visibility),保留 command 供其他
    /// 入口(若有)用,行为跟 LocalNodesButtonVisible 完全对称。
    /// </summary>
    [JsonIgnore]
    public bool NodeManagementButtonVisible => TemplateKind == "ComfyUI";

    /// <summary>
    /// v1.0.0.x #577:启停单按钮文字 — env.Status == running → "停止";其它 → "启动"。
    /// busy(starting/stopping)时按钮禁用,文字保留以反映"当前要做的事"。
    /// </summary>
    [JsonIgnore]
    public string StartStopButtonText { get; set; } = "启动";

    /// <summary>
    /// v1.0.0.x #577:启停单按钮 CanExecute — true 表示当前可点(根据 env.Status + busy)。
    /// </summary>
    [JsonIgnore]
    public bool StartStopButtonEnabled { get; set; } = true;

    /// <summary>
    /// v0.6.11+ T1:env-list 行 toggle 按钮用 — true = Requirements 已装(marker 文件存在),
    /// false = 未装。Load 末尾重新算(RequirementsInstaller.IsInstalled),不持久化(同
    /// IsComfyUiManagerInstalled pattern)。EnvironmentRepository 用 System.Text.Json
    /// 序列化,JsonIgnore 防止 SQLite 写入。
    /// </summary>
    [JsonIgnore]
    public bool IsRequirementsInstalled { get; set; }

    /// <summary>
    /// v0.6.11+ T1:toggle 按钮文字,根据 IsRequirementsInstalled 切换。
    /// </summary>
    [JsonIgnore]
    public string RequirementsButtonText { get; set; } = "装依赖";

    /// <summary>
    /// v0.6.11+ T1:env-list 行 toggle 按钮用 — true = BED 已装(BaseEnvUninstaller.IsInstalled
    /// 返回 true:BedStatus ∈ done/failed/installing),false = 未装(BedStatus == null)。
    /// Load 末尾重新算,不持久化(同 IsComfyUiManagerInstalled pattern)。
    /// </summary>
    [JsonIgnore]
    public bool IsBaseEnvInstalled { get; set; }

    /// <summary>
    /// v0.6.11+ T1:toggle 按钮文字,根据 IsBaseEnvInstalled 切换。
    /// </summary>
    [JsonIgnore]
    public string BaseEnvButtonText { get; set; } = "安装基础环境";

    /// <summary>
    /// 行 BED 列展示文本:✓ profileId / ✗ 未装 / ⏳ 装中 / ❌ profileId (reason)。
    /// WPF DataGridTextColumn 直接绑 BedDisplay;不需 INPC(env 一行 read-through)。
    /// </summary>
    public string BedDisplay => BedStatus switch
    {
        "done" => $"✓ {BedProfileId}",
        "failed" => $"❌ {BedProfileId ?? "?"} ({BedFailedReason ?? "失败"})",
        "installing" => "⏳ 装中",
        _ => "✗ 未装",
    };

    // ──────────────── v1.0.0.x:节点启动状态(供 ! 按钮 badge + dialog) ────────────────
    // EnvironmentListViewModel.Load() 末尾根据 nodeRepo.ListByEnv(env.Id) 重算;
    // 启动期 5s grace 后 ProcessLauncher NodeStartupErrorDetector 写入 ScanMeta["load_error"],
    // UI 用户重新打开 dialog 时 Load 触发重算看到新值。
    // JSON 不持久化(JsonIgnore),Runtime-only 计数。

    /// <summary>
    /// 该 env 当前 ScanMeta["load_error"] 非空的节点数。0 = 全部加载成功或从未启动过。
    /// env 行 ! 按钮 badge 显示这个数字(>0 时变红)。
    /// </summary>
    [JsonIgnore]
    public int FailedNodeCount { get; set; }

    /// <summary>
    /// 该 env 当前 ScannedNode 总数(env 行 ! 按钮 ToolTip 副文本)。
    /// </summary>
    [JsonIgnore]
    public int TotalNodeCount { get; set; }

    /// <summary>
    /// v1.0.0.x:派生布尔 — <c>FailedNodeCount > 0</c>。env 行 ! 按钮 color/tip 的 single-trigger
    /// 数据源(避免 5 个 Value DataTrigger 枚举 1..5,且 > 5 时也会正确显示红色)。
    /// </summary>
    [JsonIgnore]
    public bool HasFailedNodes => FailedNodeCount > 0;
}