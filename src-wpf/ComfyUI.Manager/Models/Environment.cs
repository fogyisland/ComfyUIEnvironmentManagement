using System;
using System.Collections.Generic;
using System.IO;
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
    /// <see cref="Settings.Templates"/> 的 key,例如 "ComfyUI" / "Forge")。
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
    /// v1.0.0.x (2026-08-30):env 的模型存放目录绝对路径(用户可在 Settings 改,
    /// 持久化到 <c>environments.models_directory</c> SQLite 列)。
    /// 用于派生 <see cref="Ltx2RequiredModels"/> 等模板特定模型路径。
    /// 老行(无该列)反序列化后为空字符串 — UI 兜底走 Settings.DefaultModelsDirectory。
    /// </summary>
    [JsonPropertyName("models_directory")]
    public string ModelsDirectory { get; set; } = "";

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
    /// v1.0.0.x:Forge env 行不显示「安装 ComfyUI Manager」按钮 —
    /// ComfyUI-Manager 是 ComfyUI 专属 custom_nodes extension,SD Web 用 extensions 体系,
    /// 没有 ComfyUI-Manager 概念。A1111 + SwarmUI 模板已下线,不再引用。
    /// 「装依赖」按钮保留(Forge 也有非 torch 依赖要装)。
    /// </summary>
    [JsonIgnore]
    public bool ComfyUiManagerButtonVisible => TemplateKind == "ComfyUI";

    /// <summary>
    /// v1.0.0.x (2026-08-29):Forge env 行不显示「装/卸依赖」按钮 ——
    /// Forge 的 pre-flight(clip + open_clip zip + requirements_versions.txt + git clone 3 repos)
    /// 由 lllyasviel/stable-diffusion-webui-forge launch_utils.py 在 launch.py 启动时 idempotent
    /// 自动跑(检查 `.forge_preflight_installed` marker 决定 skip),不需要用户在 env-list 行
    /// 手动触发。本工具手动「装依赖」按钮 = ForgePreFlightInstaller.InstallAsync ——
    /// 跟 launch_utils.py 跑同一份代码,只是把 4 件事从「每次 launch」前置到「一次性」,
    /// 用户体感反而多一个无意义按钮。
    /// ComfyUI / OpenVoice / Whisper / CoquiTTS / Bark / HunyuanVideo / LTXVideo / CogVideoX /
    /// Fooocus / HivisionIDPhotos 保留「装/卸依赖」按钮 — 它们有各自独立的
    /// requirements.txt 要 pip install。
    /// 镜像 <see cref="ComfyUiManagerButtonVisible"/> 同模式(inverse:对 ComfyUI 显示对 Forge 隐藏)。
    /// </summary>
    [JsonIgnore]
    public bool RequirementsButtonVisible => TemplateKind != "Forge";

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
    /// v1.0.0.x:Forge env 行不显示「安装本地常用」按钮 ——
    /// 「本地常用」是用户预下载的 ComfyUI custom_nodes 包(逐个 copy 到 env/custom_nodes/),
    /// Forge 用 extensions/ 体系,跟 ComfyUI custom_nodes 不兼容。
    /// A1111 + SwarmUI 模板已下线,不再引用。
    /// 镜像 <see cref="ComfyUiManagerButtonVisible"/> 同模式。
    /// </summary>
    [JsonIgnore]
    public bool LocalNodesButtonVisible => TemplateKind == "ComfyUI";

    /// <summary>
    /// v1.0.0.x:Forge env 行不显示「节点管理」按钮 ——
    /// 节点管理打开的是 <c>NodeManagementView</c>(节点扫描 / 安装 / 升级 / 卸载),
    /// 底层操作的是 env 的 custom_nodes/ 目录 + ScannedNode DB。Forge 用
    /// extensions/ 体系,跟 ComfyUI custom_nodes 不兼容,节点管理无意义。
    /// A1111 + SwarmUI 模板已下线,不再引用。镜像 <see cref="LocalNodesButtonVisible"/> 同模式。
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
    /// 行 BED 列徽章展示文本:✓ torch/version / ✗ 未装 / ⏳ 装中 / ❌ profileId (reason)。
    /// v1.0.0.x (2026-08-30):"done" 分支优先 <see cref="InstalledTorchVersion"/>(实际部署版本),
    /// fallback <see cref="BedProfileId"/>(SQLite 持久化 profile id)— 跟 <see cref="BedDisplayId"/>
    /// meta 行 priority chain 对齐,避免 badge "✓ " 空内容(BED marker 在盘上但 DB 没回填
    /// profile id 时,纯 ✓ 没信息量)。WPF Border 直接绑 BedDisplay;不需 INPC。
    /// </summary>
    public string BedDisplay => BedStatus switch
    {
        "done" => $"✓ {(InstalledTorchVersion is { } tv ? $"torch {tv}" : BedProfileId)}",
        "failed" => $"❌ {BedProfileId ?? "?"} ({BedFailedReason ?? "失败"})",
        "installing" => "⏳ 装中",
        _ => "✗ 未装",
    };

    /// <summary>
    /// v1.0.0.x (2026-08-30):BED 未装时,该 env 即将装的 torch 版本提示文本。
    /// 用于 env list meta 行(PythonVersion / BedProfileId 之后追加),让用户
    /// 在「装 BED」前就能看到该 template 锁的 torch 版本号。
    /// 当前只有 Forge 锁 torch==2.4.0(<c>ForgeBaseEnvConstants.TorchVersion</c>);
    /// A1111 / ComfyUI / HunyuanVideo / 等用 BaseEnvProfile,TorchVersion 由
    /// 用户在「装 BED」对话框里选,这里不强加 hint(留 null,UI 走 XAML 判定隐藏)。
    /// 已装 BED(<see cref="BedProfileId"/> 非空 或 <see cref="IsBaseEnvInstalled"/> true)返 null —
    /// BedDisplayId 列已显示实际状态,不重复。
    /// </summary>
    [JsonIgnore]
    public string? PendingBedHint
    {
        get
        {
            if (!string.IsNullOrEmpty(BedProfileId)) return null;
            if (IsBaseEnvInstalled) return null; // marker 文件存在但 SQLite bed_profile_id 未回填
            return TemplateKind switch
            {
                "Forge" => $"torch {ForgeBaseEnvConstants.TorchVersion} 待装",
                _ => null,
            };
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-08-30):实际部署的 torch 版本(读 &lt;venv&gt;/Lib/site-packages/torch/version.py
    /// 第一行)。BED 已装时由 <see cref="Services.TorchVersionDetector"/> 在 Load 末尾算出,
    /// 用于 <see cref="BedDisplayId"/> 优先级最高一档,显示 "torch 2.4.0+cu121" 这样的实际版本。
    /// 不可用(BED 未装 / venv 无 torch / 探测 IO 失败)时保持 null — BedDisplayId 走
    /// BedProfileId / IsBaseEnvInstalled fallback。
    /// </summary>
    [JsonIgnore]
    public string? InstalledTorchVersion { get; set; }

    /// <summary>
    /// v1.0.0.x (2026-08-30):meta 行第三段(BedProfileId 列)的展示文本 —
    /// 四态 fallback,解决 state desync 老 env 显示「未部署」误导用户 + 让用户看到实际 torch:
    /// <list type="number">
    /// <item><see cref="InstalledTorchVersion"/> 非空(BED 已装且 venv torch 可读)→ "torch X.Y.Z+cuNNN"</item>
    /// <item><see cref="BedProfileId"/> 非空(SQLite 已回填 profile id)→ 显示 profile id</item>
    /// <item><see cref="BedProfileId"/> null 但 <see cref="IsBaseEnvInstalled"/> true(老 env BED 装好
    ///       但 SQLite 没写 profile id 且 venv torch 探测失败)→ "✓ 已部署"</item>
    /// <item>其它 → null,UI 端 <c>TargetNullValue="未部署"</c></item>
    /// </list>
    /// <see cref="IsBaseEnvInstalled"/> 由 <see cref="Services.BaseEnvUninstaller.IsInstalled"/> 在
    /// EnvironmentListViewModel.Load 末尾算出(看 <c>.forge_base_env_installed</c> 等 marker),
    /// 是 BED 是否真装的 runtime truth source(老 env / 不同步 SQLite 也准)。
    /// </summary>
    [JsonIgnore]
    public string? BedDisplayId =>
        !string.IsNullOrEmpty(InstalledTorchVersion)
            ? $"torch {InstalledTorchVersion}"
            : !string.IsNullOrEmpty(BedProfileId)
                ? BedProfileId
                : IsBaseEnvInstalled ? "✓ 已部署" : null;

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

    /// <summary>
    /// v1.0.0.x (2026-08-30):LTX-2 env 启动前必检的 5 个 .safetensors 绝对路径 —
    /// HF repo <c>Lightricks/LTX-2.5</c> quick start 命令列的 distilled transformer +
    /// gemma4-12b 文本编码器 + video VAE + audio VAE + spatial upsampler。
    /// 路径约定 <c>&lt;env.ModelsDirectory&gt;/ltx-2.5/&lt;HF 子目录&gt;/&lt;model&gt;.safetensors</c>
    /// 跟 <c>hf download --local-dir &lt;ModelsDirectory&gt;</c> 一致(env.ModelsDirectory
    /// 已有 SQLite 持久化字段)。
    /// 非 LTXVideo kind / ModelsDirectory 空 → 返空(其它模板不强制)。
    /// ProcessLauncher.StartEnvAsync 跑前检查 — 缺失抛 <see cref="ModelsMissingException"/>
    /// → UI MessageBox。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Ltx2RequiredModels
    {
        get
        {
            if (TemplateKind != "LTXVideo") return Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(ModelsDirectory)) return Array.Empty<string>();
            var root = Path.GetFullPath(Path.Combine(ModelsDirectory, "ltx-2.5"));
            return new[]
            {
                Path.Combine(root, "diffusion_models", "ltx-2.5-22b-distilled-transformer-bf16.safetensors"),
                Path.Combine(root, "text_encoders", "gemma4-12b-with-proj-ltx-2.5-bf16.safetensors"),
                Path.Combine(root, "vae", "ltx-2.5-video-vae-bf16.safetensors"),
                Path.Combine(root, "vae", "ltx-2.5-audio-vae-bf16.safetensors"),
                Path.Combine(root, "latent_upscale_models", "ltx-2.5-latent-spatial-upscaler-x2-bf16-1.0.safetensors"),
            };
        }
    }
}