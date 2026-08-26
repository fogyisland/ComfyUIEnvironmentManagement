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
}