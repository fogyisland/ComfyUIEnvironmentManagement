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