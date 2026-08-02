## v0.6.5.5 — 新建环境:区分基础解释器与 venv 解释器 + 默认值继承

v0.6.5.4 dialog 自动从 settings 带出 Python 解释器,但新建第二个 env 时仍走
settings 那条路径;用户反复手填。v0.6.5.5 让 dialog 默认继承**最近一次成功创建
env** 的基础解释器;同时区分"基础解释器"(建 venv 用的 base)与"venv 解释器"
(venv/Scripts/python.exe)两个角色,Environment 模型分别持久化。

---

### 1) 新增功能

- **`Environment.BasePythonPath`**(string,必填):创建 venv 用的基础解释器路径。
  写库时等于 dialog `PythonExe` 在创建时的值;后续默认 dialog 用这条。
- **`Environment.PythonVersion`**(string,默认 `<unknown>`):venv 解释器的
  `sys.version` 字符串(创建 venv 后读出写入);**venv 版本永远等于 base 版本**
  (Python venv 模块固有事实)— base 3.10 → venv 3.10。
- **dialog 默认值继承**:下次打开新建 dialog 时,PythonExe 默认从最近一次成功
  创建 env 的 `BasePythonPath` 拉;List 空时回退 settings(行为同 v0.6.5.4)。
- **recent base 文件不存在 → 回退 settings**(行为同 v0.6.5.4 黄色提示)。
- **"应用模板"按钮仍重置回 settings**(无视 recent)— 保留 v0.6.5.4 语义。

### 2) 数据流

```
User creates env A:
  PythonExe = settings 那条 (base 3.10)
  ↓ EnvCreatorService.CreateAsync
  venv 实际生成 <env A>/venv/Scripts/python.exe
  ↓ 读 venv python sys.version 写入 env.PythonVersion = "3.10.18 ..."
  ↓ 写库 env.BasePythonPath = "settings 那条"
  ↓ dialog 关闭 (同 v0.6.5.4)

User opens dialog for env B:
  EnvironmentListViewModel.RecentBasePythonPath = env A.BasePythonPath
  ↓ CreateEnvDialog.Show(creator, settings, projectRoot, recentBase)
  ↓ CreateEnvDialogViewModel.ApplyTemplate
  ↓ PythonExe = recent (env A.BasePythonPath)
  (无 env 时:回退 settings)
```

### 3) 升级注意

- **SQLite schema 自动迁移**:`SqliteConnectionFactory.InitSchemaIfMissing` 末尾
  调 `EnsureColumn` × 2(沿用 `CatalogCacheStore.cs:103-108` 模式);老 DB 自动
  `ALTER TABLE ADD COLUMN`,数据零丢失。
- **老行兼容**:`BasePythonPath == ""` → repository 读时 fallback 到
  `PythonExecutable`;`PythonVersion == ""` → fallback `"<unknown>"`。
- **不破坏现有 v0.6.5.4 UX**:"应用模板"按钮 / Layout 切换 / ComfyuiSource 仍走
  settings。
- **venv 是 base 的派生**:`<venv>/Scripts/python.exe` 是 launcher/链接,运行时
  必须能访问 base;base 被删/移动,venv 跑不起来(本 spec 不监控、不告警、不自动
  重建,留给后续 hotfix)。

### 4) Verification

- **dotnet test:** 298 PASS + 1 SKIP / 0 FAIL(基线 v0.6.5.4 = 285 +
  EnvironmentRepositoryTests 4 + EnvCreatorServiceTests 2 +
  CreateEnvDialogViewModelTests 4 + EnvironmentListViewModelTests 3)
- **pytest version consistency:** 3 PASS(v0.6.5.4 → v0.6.5.5)
- **dotnet build Release:** 0 errors(允许 NU1900 NuGet 网络 warning)

### 5) Commits since v0.6.5.4(`2c08d94`)

```
6613178 feat(data): Environment.BasePythonPath + PythonVersion + repo schema
8127786 feat(wpf): EnvCreatorService writes BasePythonPath + PythonVersion
18acd24 feat(wpf): CreateEnvDialogViewModel recent base inheritance
6f70b94 feat(wpf): thread recentBasePythonPath through dialog and EnvListVM
<this commit> chore(release): bump to v0.6.5.5 + release notes
```

共 5 个 commit on top of v0.6.5.4 `2c08d94`(实际是 4 + 本 close-out)。

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.5` push + `gh release create` —
  等用户明确授权(沿用 v0.6.5.4 同模式)。
- **手动 GUI smoke (TBD):** 用户桌面验证(详见 release notes §5 步骤)。
- **后续 hotfix 候选**(YAGNI,本 spec 不做):base 缺失顶部提示 + "重建 venv" 动作;
  UI 上展示 venv python 版本(目前仅 Environment.PythonVersion 模型字段)。

### Lessons learned(SDD)

- **venv 是 base 派生**:Python venv 模块固有事实—`<venv>/Scripts/python.exe` 是
  launcher/链接,运行时必须能访问 base;写 spec 必须显式说明,避免后续 PR 评审时
  把"venv 跑不起来"误判为 bug。
- **`ReadVenvPythonVersionAsync` fallback `"<unknown>"` 不抛**:env 已创建成功,
  版本号只是诊断信息,失败要 swallow,不要让版本号读取失败把整个 env 创建回滚。
- **schema 升级沿用 `EnsureColumn`**:不要重写;CatalogCacheStore 已有 helper 模式,
  复制到 `SqliteConnectionFactory` 集中管理,避免分散到各 repository。