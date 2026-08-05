# v0.6.5.8 Spec: BED 部署 installing 状态写活 + 启动 reconciliation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

## 0. 背景

v0.6.5.7 把 `Environment.BedStatus` / `BedProfileId` / `BedFailedReason` 三个字段 + `BedDisplay` 计算属性接到了 UI:env 行展示 BED 列,启动按钮 BED-aware 门控(未装/装中 disabled,失败 enabled 带 tooltip),XAML 列 + ToolTip 绑定就位。

但**实际部署流程从来没把 `"installing"` 写进 SQLite**。`BaseEnvInstaller.InstallAsync` 终态回写只有 `done` / `failed` 两条分支(`Services/BaseEnvInstaller.cs:182-208`),循环结束时才一次性写。"installing" 是个**只在内存里存在过、UI 端从未真的看到过**的幻影值,所有门控(StartCommand disabled、StartTooltip 提示、用户取消按钮 enabled 等)在这个窗口里都没生效。

副作用:
- 用户在 BED 跑到一半时点同一行的"启动"按钮 — 按钮 enabled,30s 后弹"端口未 Listen"错。
- 用户在 BED 跑到一半时点同一行的"基础环境部署" — 没拦截,会启动第二次 pip install 进程,**写到同一个 venv 目录并发破坏**。
- 上次没装完(关 WPF / 强杀 / 断电)→ 行的 BedStatus 是上次的 done,跟当前 venv 真实状态脱节,启动后 import torch 失败。

## 1. Goals

- **G1**: `BaseEnvInstaller.InstallAsync` 进入单 env 的 pip 之前,把 `env.BedStatus = "installing"` 写库,**让 UI 立刻看到 ⏳ 装中** 状态。
- **G2**: 现有终态回写(done / failed + reason)行为保持不变(失败 reason 通过 `failures` 字典注入,跟现在一样)。
- **G3**: 启动时(`App.OnStartup`)做一次 **reconciliation pass**:把所有残留的 `BedStatus = "installing"` 行翻成 `failed` + `BedFailedReason = "上次未完成"`。WPF 重启后这些行不再"假装装中",启动按钮恢复到合理状态(enabled + tooltip 提示)。
- **G4**: 整个改动**只动 `BaseEnvInstaller` + `App` 两处**,不动 BaseEnvProgressDialog / EnvironmentListViewModel / 任何 XAML(因为 v0.6.5.7 的门控已经接好,只是没数据流进来)。
- **G5**: 跨进程 job 持久化(关闭 WPF 后台仍跑、跨进程 attach)不在此 spec — 那是 v0.6.5.9 的 P0-B 后台任务注册表范围。reconciliation 是 P0-A 的弱化方案:不真的后台跑,只把"已经死掉的 installing"标成 failed,让用户可以重跑。

## 2. 非 Goals

- 不实现真正的"关 dialog 后台继续跑" — 那是 P0-B `BaseEnvJobService` 范围
- 不做"deploy 进行中禁止其他 deploy 启动"互斥锁 — 那是 P0-B 范围(P0-B 的 job service 自然带 by-env-id 锁)
- 不在 installer 内部加"已 installing 拒绝启动"前置检查 — 用户手动点"基础环境部署"按钮时 EnvListVM 的 CanExecute 不变;短期内通过 reconciliation 兜住跨次运行的状态污染
- 不改 `Environment.BedStatus` 字面量集合(继续用 `"done"` / `"failed"` / `"installing"` / `null`)
- 不改 `BedDisplay` 计算属性(已经覆盖 4 个分支)
- 不改 `BaseEnvProgressDialog`(dialog 当前 ShowDialog() 阻塞返回,装完才出栈)
- 不改任何 XAML
- 不 bump version / 不发 release zip(per hotfix 偏好,无 ledger 提交)

## 3. 数据模型

**无新字段,无 schema 变更**。`Environment.BedStatus` 字符串集合扩展一个 `installing` 字面量 — 这是 v0.6.5.7 早已支持的,只是没有写路径。

## 4. 架构

### 4.1 `BaseEnvInstaller.InstallAsync` 写 installing

**`Services/BaseEnvInstaller.cs`** — 在 `pipArgs` 计算后,foreach 之前做一次"批量写 installing"是不行的,因为我们想保持每个 env 顺序推进、UI 立即看到该行的 `⏳ 装中` 状态。

**采用 per-env 写**:每个 env 进入循环后,先 `progress?.Report(... Running ...)` 之前(也就是把 env 解析到、python 路径解析成功之后,真正要 spawn pip 之前)写一次 `BedStatus = "installing"` 到 db。

```csharp
foreach (var envId in envIds)
{
    if (ct.IsCancellationRequested) { cancelled = true; break; }

    // ... 既有 env / pythonExe 解析 + 失败处理 ...

    // **G1**: 进入 pip 之前立刻写 installing,UI 立刻看到 ⏳ 装中,
    // 同一行 StartCommand 立即 disabled(已有 v0.6.5.7 门控)。
    // 单 env 写失败不致命(envRepo 不可写概率几乎 0,跟终态回写 try/catch 一致)。
    try
    {
        var live = _envRepo.Get(envId);
        if (live is not null)
        {
            live.BedStatus = "installing";
            _envRepo.Upsert(live);
        }
    }
    catch { /* 写失败不致命,继续 */ }

    progress?.Report(new BaseEnvProgress(
        BaseEnvStatus.Running, completed, total,
        envId, env.Name, 0, $"开始安装 ({env.Name})", null));

    // ... 既有 RunPipAsync + 失败/成功/取消分支 ...

    // **G2**: 终态回写(已有 v0.6.5.7 逻辑,完全不动)
    // foreach envIds 走 failures 字典 → done or failed+reason
}
```

**边界**:
- 写 installing 失败的 try/catch 跟终态回写 try/catch 一致,不抛
- "env 解析失败"分支(行 81-96)和"pythonExe 解析失败"分支(行 98-112)不进 installing 写(因为还没开始装) — 跟 v0.6.5.7 一样,这些分支直接 failed + reason 写终态
- `ct.IsCancellationRequested` 在 foreach 起始处已经 break 出去,不进 installing 写

**为什么不是更早写**:`_envRepo.Get(envId)` 失败的话根本没必要写 installing(直接进 failures dict); `GetVenvPythonPath` 失败同理。这两个 try/catch 在循环最前面,正好。

### 4.2 `App.OnStartup` 启动 reconciliation

**`App.xaml.cs:17-88`** — 在 SQLite 已建好(第 29 行 `new SqliteConnectionFactory()` 隐式建好),`MainWindow` 还没 Show 之前,做一次 reconciliation。

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // ... 既有 projectRoot / dbFactory / envRepo / settings / profileLoader / 等等 ...

    // G3: 启动 reconciliation — 把上次没装完的"installing"行翻成 failed。
    // 必须放在 main.Show() 之前(否则 UI 启动时仍看到 ⏳ 装中,几秒后变 ❌ 闪烁)。
    // 也必须放在 _mainVm 构造之前(VM 构造时会 Load() 一次,reconciliation 写完才好)。
    BaseEnvInstaller.ReconcileStaleOnStartup(envRepo);

    // ... 既有 _mainVm 构造 + main.Show() ...
}
```

**新增 `BaseEnvInstaller.ReconcileStaleOnStartup` 静态方法**:

```csharp
/// <summary>
/// 启动 reconciliation:把所有 BedStatus == "installing" 的 env 翻成
/// "failed" + BedFailedReason = "上次未完成"。
///
/// WPF 重启后没有跨进程 job 持久化,这些行只能来自:
///   1) 上次 WPF 强杀(任务管理器 / 断电 / OS 重启),pip 进程已死
///   2) 上次 WPF 正常退出但 pip 还在跑(理论上 OnExit 应 cancel + drain,
///      但 v0.6.5.6 之前没这个保证)
/// 不做更细的判断(无法知道 venv 是否真的有 torch),统一标 failed 让
/// 用户重跑,启动按钮 enabled + tooltip 提示 "上次未完成"。
/// </summary>
public static int ReconcileStaleOnStartup(EnvironmentRepository envRepo)
{
    if (envRepo is null) throw new ArgumentNullException(nameof(envRepo));

    var stale = 0;
    foreach (var env in envRepo.ListAll())
    {
        if (env.BedStatus == "installing")
        {
            env.BedStatus = "failed";
            env.BedFailedReason = "上次未完成";
            try
            {
                envRepo.Upsert(env);
                stale++;
            }
            catch
            {
                // 单行写失败不致命,下次启动再翻
            }
        }
    }
    return stale;
}
```

**为什么是 static 方法、而不是 instance 方法**:
- App.OnStartup 调用方是组合根,不需要 `BaseEnvInstaller` 实例(`RunPipAsync` 是 protected virtual,实例方法在 reconciliation 路径用不上)
- 静态方法易测:`new TestDb()` + `new EnvironmentRepository()` 直接调
- 跟 `BaseEnvInstaller.GetVenvPythonPath` 同样 static 风格,保持一致

**边界**:
- reconciliation 在 `dbFactory` 之后、`_mainVm = new MainViewModel(...)` 之前;在 sqlite migration 之后(v0.6.5.7 已 `EnsureBedColumns` 走完)
- 已经在 stale installing 的行如果 reconciliation 失败(理论上不会),下次启动再翻一次
- 跟 `BaseEnvInstaller.RunPipAsync` 的 `try { ... } catch { }` 静默吞一样,reconciliation 写失败也不致命

### 4.3 不动的地方(明确边界)

- `BaseEnvProgressDialog` / `BaseEnvProgressViewModel` — v0.6.5.7 已经接好门控(StartCommand 监听 `BedStatus`),不用动
- `EnvironmentListViewModel` — `StartCommand.CanExecute` 已经在 `BedStatus is null or "installing"` 时返 false,不用动
- `EnvironmentListView.xaml` — BED 列 + ToolTip 绑定已就位
- `Environment.BedDisplay` — 4 个 switch 分支已覆盖 installing
- `BedFailedReason` 字段已存在;just 写一个 "上次未完成" 字符串字面量
- `BaseEnvProfile` / `BaseEnvProfileLoader` / `BaseEnvViewModel` — 不涉及
- `Settings` / `SettingsView` — 不涉及
- 任何 dialog / 任何 XAML — 不涉及

## 5. UI 布局

**无 UI 改动**。v0.6.5.7 的 UI 已经支持:
- 行 BED 列:`✗ 未装` / `⏳ 装中` / `✓ profile-id` / `❌ profile-id (reason)` 四态
- 启动按钮 hover tooltip:`"基础环境未安装"` / `"基础环境安装中,请稍候"` / `"上次基础环境部署失败:..."` / 空

P0-A 修的是数据流(让 installing 真的写库 + 启动时 reconciliation),不是 UI。

## 6. Testing

### 6.1 `BaseEnvInstaller` 写 installing 测试

```csharp
// tests-wpf/.../Services/BaseEnvInstallerInstallingStateTests.cs

[Fact]
public async Task InstallAsync_WritesInstallingBeforePipRun()
{
    // seed env,bed status=null
    // fake RunPipAsync 在被调时 assert env.BedStatus == "installing"
    // 装完装入终态 done,行 BedStatus 仍是 done
}

[Fact]
public async Task InstallAsync_EnvRepoReadFailsBeforePip_DoesNotWriteInstalling()
{
    // seed env 后手动 delete SQLite 行,_envRepo.Get 返 null
    // → 不写 installing(因为根本进不到 pip),直接 failed + "env 'x' 不存在"
}

[Fact]
public async Task InstallAsync_PythonPathResolveFails_DoesNotWriteInstalling()
{
    // seed env 但 VenvPath 指向不存在的目录
    // → GetVenvPythonPath 抛 → 直接 failed,不写 installing
}

[Fact]
public async Task InstallAsync_EnvRepoUpsertFailsDuringInstalling_DoesNotAbortInstall()
{
    // envRepo 包装一层:调 Upsert 抛 SqliteException
    // → 写 installing 失败被吞,RunPipAsync 继续,整体仍 done
}
```

### 6.2 `ReconcileStaleOnStartup` 测试

```csharp
// tests-wpf/.../Services/BaseEnvInstallerReconcileTests.cs

[Fact]
public void ReconcileStaleOnStartup_FlipsInstallingToFailed()
{
    // seed 3 env:一个 installing、一个 done、一个 failed、一个 null
    // 调 ReconcileStaleOnStartup
    // → 1 个 stale,installing 行变 failed + "上次未完成",其他行不动
}

[Fact]
public void ReconcileStaleOnStartup_NullEnvRepo_Throws()
{
    // ArgumentNullException
}

[Fact]
public void ReconcileStaleOnStartup_EmptyDb_ReturnsZero()
{
    // 0 env → 0 stale,不抛
}

[Fact]
public void ReconcileStaleOnStartup_AllStale_CountsEach()
{
    // seed 5 env 全 installing → 5 stale
}
```

### 6.3 老 test 兼容性

v0.6.5.7 加的 `BaseEnvInstallerBedWriteTests` 4 个测试(全 done / 失败 failed+reason / 取消 failed / rerun 覆盖)在新代码下应该继续 PASS:
- 全 done:写 installing → 写 done(终态覆盖) → 终态 OK
- 失败 failed+reason:写 installing → 终态 failed+reason → 终态 OK
- 取消 failed+用户取消:写 installing → 终态 failed+用户取消 → 终态 OK
- rerun 覆盖:第一次 done → 第二次 installing → 第二次 done → 终态 OK

但有一个边界要小心:`BaseEnvInstallerBedWriteTests` 现有 seed 是 `BedStatus = null`(test fixture 默认),新代码会先写 `"installing"` 再写终态。`FakeBaseEnvInstaller` override `InstallAsync` 时**完全跳过了基类的写 installing 逻辑**,因为它是整体 override 不是 partial(看 `BaseEnvInstallerTests.cs:263-362` 的 `FakeBaseEnvInstaller.InstallAsync` 整个 loop 重写)。

**这意味着现有 4 个老 test 的 FakeBaseEnvInstaller 不会触发新写 installing 的代码路径**,必须新增 6.1 的 4 个测试用 partial 方式验证。验证方法:`FakeBaseEnvInstallerPartial : BaseEnvInstaller { protected override Task<PipResult> RunPipAsync(...)` 只 override pip 那段,InstallAsync 走基类。

## 7. 风险 + 权衡

| 风险 | 缓解 |
|---|---|
| 写 installing 跟终态写之间很短(几十 ms),UI 看 `⏳ 装中` 一闪而过 | 设计如此:用户进 BED 进度 dialog,主列表就显示 `⏳ 装中` 直到 dialog 关;用户可感知到 "UI 真的在变" |
| `BaseEnvInstaller.InstallAsync` 是 `virtual`,`FakeBaseEnvInstaller` 整体 override 跳过了基类写 installing 逻辑 | 新加 4 个测试用 partial fake(只 override `RunPipAsync`);老的整体 override fake 不验证 installing 写(它的目的是测 progress emit + 失败 dict,跟新行为正交) |
| 启动 reconciliation 慢(库里有几万 env?) | 现实不会:env 列表最多几十个;ListAll + foreach + 单行 Upsert 是 SQLite 毫秒级 |
| reconciliation 写入跟 `BaseEnvInstaller` 写入竞态(用户在另一个 WPF 进程跑 BED) | WPF 单进程,无竞态;以后真要做多进程再讨论 |
| "上次未完成"reason 跟真 pip 失败的 reason 混淆 | BedStatus 都是 "failed";UI 端 BedDisplay 用 `❌ {profile} ({reason})`,会显示 "❌ xxx (上次未完成)" — 用户能看出区别 |
| 改 `BaseEnvInstaller` 影响 v0.6.5.7 的 4 个老 test | 不影响:老 test 用 `FakeBaseEnvInstaller` 整体 override,新写 installing 逻辑在基类,不触发 |
| `ReconcileStaleOnStartup` 写失败被吞 → 下次启动又翻一次 | 接受,跟终态写失败的容错一致 |

## 8. 升级注意

- **直接覆盖 v0.6.5.7 文件即可**(无版本 bump,本地 hotfix per 用户偏好)
- 老 env 表无 schema 变更(v0.6.5.7 已经加过 3 列)
- 升级后首次启动会自动 reconciliation,所有 `BedStatus="installing"` 行翻成 `failed` + "上次未完成"
- 升级前如果用户正在跑 BED,升级过程中被杀 → 升级后该 env 变 `failed`,可重跑

## 9. Verification

### 单元测试
- WPF `dotnet test tests-wpf/ComfyUI.Manager.Tests/ -v minimal` → 期望 **364 PASS / 0 FAIL / 1 SKIP**(基线 358 + 新增 6:4 写 installing + 4 reconciliation - 2 重复的跟老 test 共享)

### 端到端手动测试(用户 desktop)
1. 双击 `release/staging/ComfyUI Manager/ComfyUI.Manager.exe`
2. 侧边栏"环境" → 看所有行 BED 列(若 v0.6.5.7 已经跑过,显示 ✓ / ✗ / ❌)
3. 选一个 env,点"基础环境部署" → dialog 弹出,**立刻**看主列表该行变 `⏳ 装中`
4. **同时**,hover 该行"启动"按钮 → tooltip 变 "基础环境安装中,请稍候",按钮变灰
5. 试试点同一行的"基础环境部署" → 在 v0.6.5.7 + P0-A 下还能点(互斥锁是 P0-B 范围);不点
6. 等 dialog 装完 → 主列表该行变回 `✓ {profileId}` 或 `❌ ...`
7. **强杀测试**:再起一个 BED,跑到一半用任务管理器关 WPF → 重开 WPF → 该行立刻变 `❌ ... (上次未完成)`(reconciliation 生效)
8. hover 启动按钮 → tooltip "上次基础环境部署失败:上次未完成;运行可能也失败"
9. 点启动 → 即使可能失败,流程正常,不再有"看似成功其实没装"的 ghost 状态

### 边界
- offline 启动 / 库里 0 env / 全 done / 全 failed:reconciliation 都安全(返 0)
- 库里有 100 个 env:reconciliation 仍 < 100ms

## 10. Critical files

- Modify: `src-wpf/ComfyUI.Manager/Services/BaseEnvInstaller.cs` (+ 写 installing + `ReconcileStaleOnStartup` 静态方法)
- Modify: `src-wpf/ComfyUI.Manager/App.xaml.cs` (+ 调 `ReconcileStaleOnStartup`)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerInstallingStateTests.cs` (~4 tests:partial fake)
- Create: `tests-wpf/ComfyUI.Manager.Tests/Services/BaseEnvInstallerReconcileTests.cs` (~4 tests)

## 11. 不在范围(留给后续 hotfix)

- P0-B 后台任务注册表(`BaseEnvJobService`):关 dialog 后台跑、attach / 重连
- P1-A 部署目标 env 选择 dialog
- P1-B pip 下载缓存目录配置
- P1-C CUDA 列表 / cu124 / HasCpu 修正
- P2 侧栏系统信息面板
