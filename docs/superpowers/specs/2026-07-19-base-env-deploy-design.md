# v0.6.4 Hotfix — 基础环境部署(Torch/torchaudio/torchvideo/xformers)

**里程碑:** v0.6.4 hotfix(v0.6.4 catalog 分页 + 版本号下拉 之后)
**日期:** 2026-07-19
**状态:** 待用户审阅
**Base SHA:** 当前 main HEAD(包含 v0.6.4 catalog + 版本号下拉,但 v0.6.4 release 还没推送)

---

## 0. 摘要

在 WPF UI 左侧(EnvList 页)新增"基础环境部署"按钮,允许用户挑选一个或多个 env,在该 env 的 venv 里跑 `pip install` 把 torch/torchaudio/torchvision/xformers 等基础环境依赖装好。Settings 提供结构化表单 + 高级 raw 模式两类配置入口,real-time 进度 + 可取消。

### 关键决策

| 决策 | 选择 |
|---|---|
| 入口 | EnvList 页顶部 toolbar 新增 `[基础环境部署]` 按钮 |
| env 选择 | 弹 dialog 让用户多选 |
| 包配置位置 | Settings 页新增 section "基础环境"(结构化 form + 折叠高级 raw 模式) |
| 配置形态 | `BaseEnvConfig` 嵌套对象(CudaVersion / TorchChannel / Packages / ExtraArgs / CustomPipArgs) |
| pip 命令构造 | `CustomPipArgs` 非空优先,否则 `{pip} install {pkgs} {ExtraArgs}` |
| 进度 UI | 弹第二个 dialog:整体 N/M env 完成 + 当前 env 进度 + 滚动日志 + 取消 |
| 单 env 失败处理 | 默认继续下个 env,UI 红色显示该 env 失败 |
| 取消 | 立即 kill 当前 pip 进程,后续 env 跳过,弹 dialog 顶部"已取消" |

### 不动的东西

- 现有 `Settings` 顶层字段(只新增嵌套 `BaseEnvConfig` 对象,默认值兼容老的 `Settings.json`)
- 现有 `CatalogRefreshService` / `NodeOperations` / `GitRunner`
- 现有 EnvListView 其他功能(start/stop/log/install/uninstall)

---

## 1. 目标 & 非目标

### 1.1 目标(本次完成时)

- WPF 左栏 EnvListView 顶部 toolbar 新增按钮"基础环境部署"(icon/text),默认可见
- 点击 → 弹 `BaseEnvDialog`(模态):
  - 左半:env 多选列表(checkbox),默认全不选
  - 右半:包配置表单(CUDA 下拉 + 通道下拉 + 包列表 + ExtraArgs),点击"高级"折叠可编辑 raw pip args
  - 底部"预览 pip 命令"按钮 → 弹只读文本框显示拼出的命令
  - 底部"开始安装" / "取消"
- 点"预览"按钮 → 看到当前配置将跑的 args(只读 TextBox 多行展示,例如:
  ```
  python: <path-to-venv-python>
  args  : install torch torchaudio torchvision xformers --index-url https://download.pytorch.org/whl/cu118
  ```
  )
- 配置值来源 = Settings.BaseEnvConfig(Dialog 只是展示 + 用一份本地副本)
- "开始安装" → 关闭 BaseEnvDialog,弹 `BaseEnvProgressDialog`(模态)
- `BaseEnvProgressDialog` 显示:
  - 整体进度:`已完成 N / 总 M` + 进度条(按 env 数等分)
  - 当前 env 状态:`env-b — pip 正在下载 torch`
  - 当前 env 进度条:从 pip stdout 抓百分比(`/Progress \((\d+\.\d+)\/|/ (\d+\.\d+) \/ /`)
  - 滚动日志(scroll-to-bottom,只读)
  - "取消"按钮
- 安装流程(后台 Task):
  - 对每个 env 顺序跑 `pip install <args>`(Process.Start venv python.exe + -m pip)
  - emit `BaseEnvProgress { Stage, EnvId, EnvName, Completed, Total, Percent, LogLine, Status }`
  - 单 env 失败:emit `EnvFailed`,继续下个(可被取消打断)
  - 全部完成或取消后:弹 dialog 顶部状态条变化
- Settings 页新增 section "基础环境":
  - 简单 mode:CUDA 下拉 + 通道下拉 + 包 ListView(添加/删除)+ ExtraArgs TextBox
  - "高级"折叠:Raw pip args TextBox
- 所有 Settings 字段持久化(写回 Settings.json via `SettingsRepository.Save`)
- 默认值:torch/torchaudio/torchvision/xformers,cu118,stable

### 1.2 非目标(本次不做)

- 不做 NVIDIA Driver 自动检测(GPU 探测不影响命令构造)
- 不做多 Python 版本切换
- 不做 conda 支持(只用 pip)
- 不做 per-env 隔离配置(每个 env 共享同一个 BaseEnvConfig)
- 不做"全部 pip 包可视化管理"(只管 torch/xformers 这一类)

---

## 2. 文件改动表

### 新增

| 文件 | 职责 |
|---|---|
| `Models/BaseEnvConfig.cs` | `BaseEnvConfig` record: CUDA / Channel / Packages / ExtraArgs / CustomPipArgs |
| `Services/BaseEnvInstaller.cs` | 跑 `pip install` per env,emit Progress,支持 CancellationToken |
| `Services/BaseEnvProgress.cs` | `BaseEnvProgress` record + `BaseEnvEnvStatus` enum + `BaseEnvInstallResult` |
| `Views/BaseEnvDialog.xaml` + `.cs` | 包配置 + env 多选 + 预览 dialog |
| `Views/BaseEnvProgressDialog.xaml` + `.cs` | 实时进度 + 日志 + 取消 dialog |
| `ViewModels/BaseEnvDialogViewModel.cs` | Dialog 数据 + 校验 + 命令构造 |
| `ViewModels/BaseEnvProgressViewModel.cs` | 订阅 installer 事件,更新 UI |
| `tests-wpf/.../Services/BaseEnvInstallerTests.cs` | 单测:fake venv,验证命令构造 + 进度语义 + 取消 |

### 修改

| 文件:行 | 改动 |
|---|---|
| `Models/Settings.cs` | 加 `public BaseEnvConfig BaseEnv { get; set; } = new();` |
| `Views/EnvListView.xaml` | toolbar 新增按钮 `[基础环境部署]`,Command 绑到 MainViewModel.BaseEnvCommand |
| `ViewModels/MainViewModel.cs` | ctor 接 `Action onShowBaseEnvDialog`,新增 `BaseEnvCommand`(只 RaiseCanExecuteChanged) |
| `Views/SettingsView.xaml` | 加"基础环境"section + BaseEnvSettingsViewModel |
| `ViewModels/SettingsViewModel.cs` | `BaseEnvPackages` ObservableCollection<string> + `BaseEnvCuda` 等字段,改写 `Save()` 把 BaseEnv 写回 |
| `App.xaml.cs` | 注册 BaseEnvInstaller(MainViewModel ctor + 1 个依赖) |
| `Themes/Theme.xaml` | 加 BaseEnvDialog / BaseEnvProgressDialog 共用 style(可选) |

---

## 3. 接口契约

### 3.1 `Models/BaseEnvConfig.cs`

```csharp
public class BaseEnvConfig
{
    public string CudaVersion { get; set; } = "cu118";   // cu118 / cu121 / cu124 / cpu
    public string TorchChannel { get; set; } = "stable";  // stable / nightly
    public List<string> Packages { get; set; } = new()
        { "torch", "torchaudio", "torchvision", "xformers" };
    public string ExtraArgs { get; set; } = "";          // --index-url ... / -f ... 等
    public string CustomPipArgs { get; set; } = "";      // 高级:整段覆盖,优先

    /// <summary>
    /// 把当前配置拼成 `pip install ...` 的 args 列表(argparse 风格)。
    /// CustomPipArgs 非空 → 直接 split 后返回,优先级最高。
    /// </summary>
    public IReadOnlyList<string> BuildPipArgs()
    {
        if (!string.IsNullOrWhiteSpace(CustomPipArgs))
            return CustomPipArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var args = new List<string> { "install" };
        args.AddRange(Packages);
        if (TorchChannel == "nightly")
            args.Add("--pre");
        if (!string.IsNullOrWhiteSpace(CudaVersion) && CudaVersion != "cpu")
        {
            args.Add("--index-url");
            args.Add($"https://download.pytorch.org/whl/{CudaVersion}");
        }
        if (!string.IsNullOrWhiteSpace(ExtraArgs))
            args.AddRange(ExtraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return args;
    }
}
```

### 3.2 `Services/BaseEnvProgress.cs`

```csharp
public enum BaseEnvStatus { Pending, Running, Succeeded, Failed, Cancelled }

public record BaseEnvProgress(
    BaseEnvStatus Status,
    int Completed,        // 已完成 env 数(成功/失败都算)
    int Total,            // 总 env 数
    string? CurrentEnvId, // 当前正在跑的 env id
    string? CurrentEnvName,
    int? EnvPercent,      // 当前 env 的内部进度 0-100
    string? LogLine,      // 新一行 log(可空)
    string? ErrorMessage  // 仅 Failed 时非空
);

public record BaseEnvInstallResult(
    bool Cancelled,
    int SucceededCount,
    int FailedCount,
    IReadOnlyDictionary<string, string> Failures  // envId → reason
);
```

### 3.3 `Services/BaseEnvInstaller.cs`

```csharp
public class BaseEnvInstaller
{
    public BaseEnvInstaller(GitRunner git /* 共用 venv 解析 helper */) { ... }

    /// <summary>
    /// 顺序跑 base env install 跨多个 env,emit progress。
    /// 单 env 失败不中断,继续下个。CancellationToken 触发时立即 kill 当前 pip。
    /// </summary>
    public async Task<BaseEnvInstallResult> InstallAsync(
        IReadOnlyList<string> envIds,
        BaseEnvConfig config,
        IProgress<BaseEnvProgress>? progress = null,
        CancellationToken ct = default);

    public static string GetVenvPythonPath(Environment env);  // 复用 NodeOperations 里类似逻辑

    private async Task<EnvRunResult> InstallOneAsync(
        string envId, BaseEnvConfig config,
        IProgress<BaseEnvProgress> progress, CancellationToken ct);
}
```

### 3.4 `Models/Settings.cs`(增量)

```csharp
public BaseEnvConfig BaseEnv { get; set; } = new();
// 兼容老的 settings.json:反序列化时 BaseEnv 缺失 → 用 new() 默认值
```

### 3.5 `ViewModels/BaseEnvDialogViewModel.cs`(主接口)

```csharp
public class BaseEnvDialogViewModel : ViewModelBase
{
    public ObservableCollection<EnvChoice> Envs { get; }   // CheckBox 形态,IsChecked 双向
    public BaseEnvConfig Config { get; }                   // 副本(用户可改不影响 Settings)
    public string PreviewCommandText { get; }              // "预览 pip 命令" 计算属性

    public RelayCommand AddPackageCommand { get; }
    public RelayCommand RemovePackageCommand { get; }      // param: string package
    public RelayCommand StartCommand { get; }              // 校验:至少 1 env 选 + 至少 1 package
    public RelayCommand CancelCommand { get; }

    public IEnumerable<string> CudaVersions { get; }      // cu118 / cu121 / cu124 / cpu
    public IEnumerable<string> TorchChannels { get; }      // stable / nightly

    public event Action<IReadOnlyList<string>, BaseEnvConfig>? InstallRequested;
}

public record EnvChoice(Environment Env, bool IsChecked);
```

### 3.6 `ViewModels/BaseEnvProgressViewModel.cs`

```csharp
public class BaseEnvProgressViewModel : ViewModelBase
{
    public int Completed { get; }     // bind 整体进度
    public int Total { get; }
    public int EnvPercent { get; }    // 当前 env 内部
    public string StatusText { get; } // "env-b — 正在下载 torch"
    public string LogTail { get; }    // last N lines,追加式
    public BaseEnvStatus OverallStatus { get; }   // 顶部红/绿条

    public RelayCommand CancelCommand { get; }

    public void OnProgress(BaseEnvProgress p);  // 从 Progress<T>.Callback 调用
}
```

---

## 4. UI 行为

### 4.1 EnvList 按钮 → BaseEnvDialog
- 按钮文字:`基础环境部署`
- 位置:toolbar 左数第 3 个(列表视图 / 磁贴视图 toggle 之后,刷新按钮之前)
- 点击:实例化 `BaseEnvDialog`,DataContext = `BaseEnvDialogViewModel(envs, settings.BaseEnv)`
- 用户选 env + 改 config(本地副本),点"开始安装"→ 触发 `InstallRequested` 事件 → MainViewModel 关 dialog,弹 ProgressDialog
- 点"取消"或 ESC → 关 dialog 无副作用

### 4.2 ProgressDialog 互动
- 整体进度 = `Completed / Total`,end state:
  - 全 succeeded:绿色 "完成"
  - 全 failed/cancelled:红色
  - 混合:黄色 "已完成 N,失败 M"
- 取消按钮:弹"确认"prompt(简单 MessageBox)→ 确认后 CancelCommand.Execute → `installer.Cancel()` → `BaseEnvInstaller` 删当前 child process 并抛 CancellationToken
- 完成后"关闭"按钮或 X 关闭 dialog(用 Dispatcher)

### 4.3 Settings 持久化
- 用户点 Settings 页"保存"或"应用" → 写回 `settings.BaseEnv = BaseEnvDialogViewModel.Config`
- 默认值已通过 `new BaseEnvConfig()` 初始化 → 老 settings.json 无 BaseEnv 字段也能正确反序列化

---

## 5. 测试

### 5.1 `BaseEnvConfigTests`
- `BuildPipArgs_CustomPipArgs_ReturnsSplit`：CustomPipArgs=`"install torch --pre"`,返回 `["install","torch","--pre"]`
- `BuildPipArgs_StableCu118_BuildsIndexUrl`：默认 → 含 `--index-url https://download.pytorch.org/whl/cu118`
- `BuildPipArgs_Nightly_AppendsPreFlag`：TorchChannel=`nightly` → 含 `--pre`
- `BuildPipArgs_Cpu_NoIndexUrl`：CudaVersion=`cpu` → 不含 `--index-url`
- `BuildPipArgs_ExtraArgs_Appended`：ExtraArgs=`"--user"` → 含

### 5.2 `BaseEnvInstallerTests`
- 用 fake `GitRunner`(包装 fake `Process.Start`),跑 `InstallOneAsync(envId, config, ...)`
- 验证:pip 命令构造正确(`python.exe` 路径 + args)
- 验证:progress emit 多次(含 Started/LogLine/Completed)
- 验证:Cancel → 立即 Kill fake process,installer 返回 cancelled result
- 验证:多 env 顺序跑,一个 fail 不影响下个

### 5.3 不做 E2E
pip install 跑真实 venv 太慢且依赖网络,单测足够。手动验证留给用户。

---

## 6. 风险 & 权衡

| 风险 | 缓解 |
|---|---|
| pip 下载慢,UI 假死 | Background Task + IProgress + Dispatcher 更新 |
| pip 进程 kill 后留半截 venv 文件 | 用户手动 `pip check` 即可,不在本次范围 |
| pip stdout 格式变化导致 percent 抓不到 | 对 percent 失败时只显示 log,不显示百分比 |
| 用户自定义 raw args 写错 | "预览"按钮一定显示最终命令,避免盲目 |
| NVIDIA 不支持的 CUDA(老卡) | 用户在 Settings 改 cu118 / cpu,默认 cu118 兼容多数 |
| 多 env 并行装同一组包浪费带宽 | 默认串行(本次设计) |
| Settings 中 BaseEnv 字段丢失 | 反序列化兜底 `new()` |

---

## 7. 用户需决策的点

1. ✓ 入口:EnvList toolbar "基础环境部署" 按钮 — 见摘要
2. ✓ env 选择:弹 dialog 多选 — 已确认
3. ✓ 配置形态:简单 form + 高级 raw — 已确认
4. ✓ 进度:实时 + 取消 — 已确认
5. 范围估计:~5-6 个新文件 + ~6 个修改,~800-1200 行,5-8 个 commit

---

## 8. 验收(给用户)

1. 双击 ComfyUI Manager.exe → 进 env 列表 → 看到顶部"[基础环境部署]"按钮
2. 点击 → 弹 BaseEnvDialog:左侧 env 多选(默认空),右侧 CUDA/包/高级设置
3. 选 1 个 env,不动配置,点"预览" → 看到 `pip install torch torchaudio torchvision xformers --index-url https://download.pytorch.org/whl/cu118`
4. 点"开始安装" → 关 dialog,弹 ProgressDialog
5. 真实跑 venv `pip install` × 4 包(用 v0.6.x 内置 venv)
6. 进度条动,log 滚动,能取消(测一下取消按钮)
7. 完成后 dialog 绿色"已完成"
8. 去 Settings → "基础环境"section → 改 CUDA 到 `cu121` → 保存 → 重启 app → BaseEnvDialog 的 CUDA 下拉显示 `cu121`
9. 切到"高级"折叠 → 改 `CustomPipArgs` → 预览显示新的 raw 命令 → 保存

---

## 9. 后续(本次范围外)

- 自动 GPU 探测 → 自动选 CUDA
- 多 Python 版本切换
- "诊断"按钮:跑 `pip check` / `python -c "import torch; print(torch.cuda.is_available())"`
- v0.7+ 加 conda 支持
