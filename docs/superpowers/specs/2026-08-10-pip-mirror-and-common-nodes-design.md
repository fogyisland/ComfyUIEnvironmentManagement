# v0.6.11++ 「Settings 加 pip 镜像源 + 常规节点配置」Design Spec

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` to implement this plan task-by-task.

## Goal

在 Settings 加两个独立但相关的配置项:

1. **Pip 镜像源** — 用户在 Settings 选 PyPI 镜像(官方 / 清华 TUNA / 阿里云 / USTC / 自定义),所有 ComfyUI / ComfyUI Manager 依赖安装都走这个镜像。BED (BaseEnvInstaller) 不受影响,继续走 pytorch.org 拿 CUDA wheels。
2. **常规节点** — 用户在 Settings 勾选一组"环境管理用、不冲突的常用节点",env-create 末尾 + 装依赖 末尾自动 clone 这些节点到 `<env>/ComfyUI/custom_nodes/`。

## Architecture

- **Settings 单一来源** — 加 2 个 string 字段(`PipMirror` / `PipMirrorCustomUrl`) + 1 个 List<CommonNodeEntry>(`CommonNodes`)+ 1 个 enum(`PipMirrorKind`)+ 1 个 model class(`CommonNodeEntry`)。Settings 用现有 JSON 持久化(`<appdata>/ComfyUI-Manager/settings.json`),无需迁移。
- **Pip 镜像作用面** — 走 `RequirementsFileInstaller`(覆盖 ComfyUI requirements.txt + Manager requirements.txt 两处)。`BaseEnvInstaller` 改 `BuildPipArgs()` 不动(继续发 `--index-url https://download.pytorch.org/whl/{cuda}`,因为 TUNA/Aliyun/USTC 不镜像 pytorch.org 的 CUDA wheels)。`RequirementsUninstaller`(pip uninstall)不需要 --index-url。
- **Lazy 解析** — `RequirementsFileInstaller` 接 `Func<string?>? resolveIndexUrl`(不是 Settings 直接引用),每次 `InstallAsync` 调用时重新求值,所以 Settings 里改镜像立即影响下次 pip 调用,不用重启应用。
- **常规节点触发点** — 2 个 hook:`EnvCreatorService.CreateAsync` step 5.7(env-create 末尾,best-effort,不阻断 env-create)+ `RequirementsInstaller.InstallAsync` 在 AutoInstallComfyUiManagerAsync 之后(装依赖末尾,best-effort,不阻断 requirements)。
- **Idempotent** — CommonNodeInstaller 对每个 enabled 节点检查 `<env.ComfyuiSource>/custom_nodes/<repo-name>` 是否存在,存在则跳过(不 `git pull`)。重新跑 env-create / 装依赖 都是安全的。

## Tech Stack

WPF .NET 8 / C# 12 · xUnit · System.Text.Json(已有)· GitRunner(已有)· 手写 MVVM (ViewModelBase / RelayCommand)· 现有 BaseEnvProfileLoader / SettingsDefaults 模式

**base SHA:** `8b536b6`(v0.6.11+ ComfyUI Manager toggle T5 commit)

## Context

v0.6.11+ ComfyUI Manager toggle SDD 完成后,用户桌面提了两个新需求:

1. **Pip 镜像**:"增加一个 requirements 库的镜像设置功能,放在设置里面"。Scope 用户选 global(影响所有 pip install,BED 例外因为 pytorch.org CUDA wheels 不在 PyPI 镜像里)。
2. **常规节点**:"另外我们添加一些常规节点安装,常规节点基本上不和其他节点冲突,用于环境管理,我们可以在设置中进行常规节点设置"。Scope 用户选:内置勾选列表 + 自由添加 + env-create + 装依赖 都触发。

保留所有现有功能(6 侧栏入口、4 顶部菜单、所有 dialog);不引入新依赖;不重新设计已工作子系统。

## Global Constraints

| # | Constraint | Source |
|---|---|---|
| **G1** | **Settings 走 JSON 持久化** — 新字段加在 `Settings.cs`,现有 `SettingsRepository.Load/Save` 自动覆盖。无需迁移代码(读时 `Enum.TryParse` 失败回退官方;读 `CommonNodes` 走 `?? new()`)。 | 现有约定 |
| **G2** | **WPF Setter + DynamicResource hard rule** — 所有新 `Setter` 用 property-element + `DynamicResource` 形式(不 `Setter Property="..." Value="{StaticResource ...}"`)。v0.6.9.2 教训。 | MEMORY |
| **G3** | **Lazy mirror 解析** — `RequirementsFileInstaller` 接 `Func<string?>?`,不接 Settings 直接引用。Settings 改值后下次调用立即生效(不用重启)。 | 用户期望 |
| **G4** | **Pip 镜像不影响 BED** — `BaseEnvInstaller.BuildPipArgs()` 不动(继续发 pytorch.org CUDA wheels)。`RequirementsFileInstaller` 接受 `--index-url` 参数但 BED 不经过 `RequirementsFileInstaller`。 | 用户决策 |
| **G5** | **CommonNodeInstaller 是 best-effort** — 不阻断 caller(env-create / 装依赖)。失败逐节点 WARN + status panel `warn:` 行,caller 仍返回 success。 | 跟 T5 AutoInstall ComfyUI Manager 同模式 |
| **G6** | **Idempotent 安装** — dir 已存在则跳过,不 `git pull`。clone 只走 `--depth=1`(浅克隆,省时间)。 | 用户决策 |
| **G7** | **不引入新依赖** — 复用 `GitRunner` / `RequirementsFileInstaller` / `NodeOperationResult` 等现有基建。 | 项目惯例 |
| **G8** | **测试覆盖** — PipMirrorResolver 8 单元测试 + RequirementsFileInstaller mirror passthrough 3 测试 + CommonNodeInstaller 5 测试 + Settings UI 集成(走现有 SettingsViewModel 测试 pattern)+ EnvCreatorService / RequirementsInstaller ctor 适配 + 全套 baseline 不退化。 | 项目惯例 |
| **G9** | **每 task 单独 commit + 单独 SDD subagent dispatch + task reviewer**,严格匹配 `progress.md` ledger。 | SDD 流程 |
| **G10** | **不做无关重构** — 不重命名公开 API;不调整既有 Settings 字段顺序。 | 项目惯例 |
| **G11** | **Built-in 节点不可删** — `IsBuiltIn=true` 的条目 UI 上删除按钮禁用(避免用户破坏 curated 列表)。仍可取消勾选 Enabled=false(等价于"不装")。 | 用户期望 |
| **G12** | **User-added 节点 Id 必须含 `/`** — UI 添加表单校验,不通过则红字提示 + 不加入列表。 | 防错 |
| **G13** | **SettingsDefaults.Apply 首次启动种 curated 列表** — 只在 `CommonNodes.Count == 0` 时 seed,保护用户的清空操作。 | 防覆盖 |

## Task Breakdown

### Task 1: Pip mirror (Settings 字段 + UI + 服务 + DI)

**Files**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ `PipMirrorKind` enum,`PipMirror` string 默认 `"official"`,`PipMirrorCustomUrl` string 默认 `""`)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ `PipMirror` / `PipMirrorCustomUrl` properties + `PipMirrorKinds` List + `IsCustomPipMirrorSelected` computed + `RaiseAllPropertiesChanged` 加新)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ Section "Pip 镜像": ComboBox 5 选项 + TextBox Custom URL(Visible only when `IsCustomPipMirrorSelected`)+ 灰色说明 + 跟现有 Section 间距一致)
- Create: `src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs` (静态 helper,`ResolveIndexUrl(Settings) → string?` + `BuildPipArgs(Settings) → IReadOnlyList<string>`)
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs` (ctor 加 `Func<string?>? resolveIndexUrl = null` 参;InstallAsync 末尾把 `--index-url <url>` 拼到 pipArgs,只在 func 返回非 null 时)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (reqFileInstaller 构造改成 `new RequirementsFileInstaller(() => PipMirrorResolver.ResolveIndexUrl(settings))`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs` (8 测试:Official→null,TUNA/Aliyun/USTC→对应 URL,Custom+URL→trimmed,Custom+empty→null,Garbage→null,BuildPipArgs 空 vs 2 元素)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs` (3 新测试:null Func 不传 --index-url,Func 返回 URL 时拼接,Func 多次调用 live 重新求值)

### Task 2: Common nodes (Settings 字段 + UI + 服务)

**Files**
- Modify: `src-wpf/ComfyUI.Manager/Models/Settings.cs` (+ `CommonNodeEntry` class [Id/DisplayName/IsBuiltIn/Enabled] + `CommonNodes` List<CommonNodeEntry>)
- Modify: `src-wpf/ComfyUI.Manager/Infrastructure/SettingsDefaults.cs` (`Apply` 末尾 `s.CommonNodes = SeedCommonNodesIfEmpty(s.CommonNodes)`,10 个 curated entries)
- Modify: `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs` (+ `CommonNodes` ObservableCollection + `AddCommonNodeCommand` / `RemoveCommonNodeCommand`(gated `!IsBuiltIn`)+ `NewCommonNodeId` / `NewCommonNodeDisplayName` 表单字段 + 校验 Id 含 `/`)
- Modify: `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml` (+ Section "常规节点": ItemsControl(CheckBox + DisplayName + Id + 删除按钮[gated `!IsBuiltIn`])+ "添加节点" inline form + Id/DisplayName TextBox + Id 校验错误显示)
- Create: `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs` (`InstallEnabledAsync(env, progress, ct)` — 遍历 enabled 节点,idempotent 跳过已装,失败逐节点 WARN,aggregate 结果)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs` (5 测试:empty list,无 ComfyuiSource,正常 clone,部分失败 aggregate,取消 throw)

**Curated built-in list (10 entries)**:
```
ComfyUI-Manager (ltdrdata/ComfyUI-Manager)
ComfyUI-Impact-Pack (ltdrdata/ComfyUI-Impact-Pack)
ComfyUI-Inspire-Pack (ltdrdata/ComfyUI-Inspire-Pack)
ComfyUI-Custom-Scripts (pythongosssss/ComfyUI-Custom-Scripts)
rgthree-comfy (rgthree/rgthree-comfy)
efficiency-nodes-comfyui (jags111/efficiency-nodes-comfyui)
ComfyUI-VideoHelperSuite (Kosinkadink/ComfyUI-VideoHelperSuite)
ComfyUI-KJNodes (kijai/ComfyUI-KJNodes)
ComfyUI-Florence2 (kijai/ComfyUI-Florence2)
ComfyUI-Advanced-ControlNet (Kosinkadink/ComfyUI-Advanced-ControlNet)
```

### Task 3: Hooks (env-create 末尾 + 装依赖 末尾)

**Files**
- Modify: `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs` (ctor 加 `CommonNodeInstaller` 参;`CreateAsync` 末尾 step 5.7 调用 + try/catch + WARN log)
- Modify: `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs` (ctor 加 `CommonNodeInstaller` 参;`InstallAsync` 在 `AutoInstallComfyUiManagerAsync` 之后调用 + try/catch swallow)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (构造 `CommonNodeInstaller(settings, gitCloneAdapter, logger)` + 注入 EnvCreatorService + RequirementsInstaller ctor)
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs`(如存在)+ 其他 EnvCreator 调用方(grep `new EnvCreatorService` 跨 tests-wpf 加第 N 参数)— 验证 hook 触发顺序
- Modify: `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`(`FakeRequirementsInstaller` 加 CommonNode 字段 + `InstallAsync` override 末尾调用 + 验证)

## Critical Files (full list)

**Modified:**
- `src-wpf/ComfyUI.Manager/Models/Settings.cs`
- `src-wpf/ComfyUI.Manager/ViewModels/SettingsViewModel.cs`
- `src-wpf/ComfyUI.Manager/Views/SettingsView.xaml`
- `src-wpf/ComfyUI.Manager/Services/RequirementsFileInstaller.cs`
- `src-wpf/ComfyUI.Manager/App.xaml.cs`
- `src-wpf/ComfyUI.Manager/Data/SettingsDefaults.cs`
- `src-wpf/ComfyUI.Manager/Services/EnvCreatorService.cs`
- `src-wpf/ComfyUI.Manager/Services/RequirementsInstaller.cs`

**Created:**
- `src-wpf/ComfyUI.Manager/Services/PipMirrorResolver.cs`
- `src-wpf/ComfyUI.Manager/Services/CommonNodeInstaller.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/PipMirrorResolverTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/CommonNodeInstallerTests.cs`

**Modified tests:**
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsFileInstallerTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/RequirementsInstallerTests.cs`
- `tests-wpf/ComfyUI.Manager.Tests/Services/EnvCreatorServiceTests.cs` (or wherever EnvCreatorService tests live — grep)

## End-to-end Verification

```bash
# T1 验证 (worktree / main after commit)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal   # 0/0
dotnet test tests-wpf/ComfyUI.Manager.Tests/ --filter "FullyQualifiedName~PipMirror|FullyQualifiedName~RequirementsFileInstaller" -v minimal   # 全 PASS
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build   # 821+/0/1 baseline + new tests

# T2 验证 (在 T1 commit 上)
# 重复上面 build + test,filter 改为 FullyQualifiedName~CommonNode

# T3 验证 (在 T2 commit 上)
# 重复上面 build + test,filter 改 FullyQualifiedName~EnvCreatorService|FullyQualifiedName~RequirementsInstaller

# 全套验证 (T1+T2+T3 合并后)
dotnet build src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -v minimal
dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal --no-build
dotnet publish src-wpf/ComfyUI.Manager/ComfyUI.Manager.csproj -c Release -r win-x64 --self-contained true -o "release/staging/ComfyUI Manager" -v minimal
```

**GUI smoke(桌面验证,user):**
1. 启动 staging → Settings → 看到 2 个新区段(Pip 镜像 + 常规节点)
2. Pip 镜像 = 清华 TUNA → 关 Settings → 装依赖 → 看 pip stdout 含 `Looking in indexes: https://pypi.tuna.tsinghua.edu.cn/simple`
3. 装 BED → pip stdout 仍走 `https://download.pytorch.org/whl/cu118`(BED 不变)
4. 回到 Settings → Pip 镜像 = 自定义 → 填 `https://pypi.doubanio.com/simple` → 装依赖 → 看 stdout 走 doubanio
5. Settings → 常规节点 → 取消所有勾选 → 保存
6. 重启 → Settings → 看到 10 个 built-in 节点(全部 enabled=false)— 验证 seed 不重新覆盖
7. 勾选 ComfyUI-Impact-Pack → 新建 env → 启动后 `<env>/ComfyUI/custom_nodes/ComfyUI-Impact-Pack/` 有 `.git/`
8. 已建 env 再点装依赖 → status panel 末尾 `info:已装,跳过 ComfyUI-Impact-Pack`(idempotent)
9. 添加自定义节点 `foo/bar`(Id 含 /)→ 加入列表;尝试添加 `bar`(无 /)→ 红字提示拒绝
10. 暗/亮主题切换 → Settings 页新 section 颜色跟随(v0.6.9.2 教训 + v0.6.10.2 DynamicResource 沿用)

## Risks

| 风险 | 缓解 |
|---|---|
| 现有用户的 settings.json 没 `PipMirror` / `CommonNodes` 字段 → 读时 null | `Enum.TryParse` 失败回退官方;`CommonNodes` 走 `?? new()` + SettingsDefaults seed |
| 用户加 50 个 common nodes → env-create clone 50 个 repo 几分钟 | UI 显节点数;status panel 显进度。User-side 控制。 |
| private repo `https://github.com/foo/bar` 失败 | 单节点 Fail + WARN;不停后续;env-create / 装依赖 不阻断 |
| clone 完但 custom_node 自己有 requirements.txt 要装 | v1 不处理(用户手动触发装依赖,该 node 的 reqs 会随 env 整体装),UI tooltip 说明 |
| Built-in seed 跟用户自定义 Id 冲突 | UI 校验 Id 唯一性 + 表单红字 |
| SettingsDefaults.Apply 二次启动覆盖用户清空的 CommonNodes | seed 只在 `Count == 0` 时跑(G13) |
| User-added Id 格式错(无 `/`) | UI 校验拒绝加入(G12) |
| Mirror URL 改时正在跑的 pip | pip 用 args 快照,不 live 重新求值;下次调用 pick up 新值 |
| SettingsView.xaml 加 2 section 后页面变长 | ScrollViewer 已存在,无 layout break;所有新 Setter 强制 DynamicResource(G2) |
| T3 改 EnvCreatorService / RequirementsInstaller ctor 影响老测试 | 同 T1/T2/T5 模式:grep `new EnvCreatorService(` / `new RequirementsInstaller(` 跨 tests-wpf 加参数;Fake 子类适配新字段 |
| `Func<string?>` lazy 解析在测试里 mock 麻烦 | 测试用 `Func<string?>(() => "...")` 直接构造 lambda,不需要 mock framework |
| GitHub 限流:用户开 TUNA 但 Manager clone 走 git,git 不受 PyPI 镜像影响(直连 github) | 这是用户预期的,GitHub 限流是 git clone 的固有问题;不通过镜像解决 |
| CommonNodeInstaller 需要 git.exe | 复用 App.xaml.cs 已有 gitExe + GitRunner;不引入新依赖 |
| `Environment.ComfyuiSource` 可能为空(用户建 env 后改 root) | CommonNodeInstaller 检测空就返回 Fail "env 无 ComfyuiSource,跳过常用节点";caller WARN |

## Execution Choice

**Subagent-Driven Development(沿用项目惯例):**
- 3 task × (implementer + reviewer) ≈ 6 dispatch
- T1 → T2 → T3 串行 commit on main,每个 task 后立即 task-review
- T3 完成 → final whole-branch review (opus) → staging rebuild → MEMORY update

(plan agent left out: 用户已通过 4 节设计确认全范围 + 5 个 scope 决策问全回答;spec 即最终设计。下一步进入 plan/SDD 实施模式 → T1 implementer dispatch 起步,然后 T2 → T3 串行 subagent。)
