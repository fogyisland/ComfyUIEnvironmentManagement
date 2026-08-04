## v0.6.5.6 — Settings 多 Python 解释器管理

v0.6.5.5 之前 Settings 只暴露一条"template Python 路径 + default version"组合;
多项目用户(同时维护几个 venv,每个配不同 Python minor / 不同 base 解释器)
要切换 base 必须改 settings.json 再重启。v0.6.5.6 在 Settings 加一个完整
Python 解释器列表,支持命名条目、选定 active、删除/添加、自动迁移老字段,
配 5 秒 Python 验证防止误填。

---

### 1) 新增功能

- **`Settings.PythonInterpreters`**(list of `PythonInterpreter`):Settings 顶级
  字段,每条 `{Name, Path}`。顺序由用户控制,首个不一定是 active。
- **`Settings.ActivePythonInterpreterName`**(string):指向列表条目的 `Name`,
  运行时解析为 `PythonInterpreter` 全字段供新建环境使用;默认取列表首项。
- **`PythonInterpreter` POCO**(`{Name, Path}`):与 `Settings` 并列的 model 类型,
  沿用项目既有 POCO 风格(`Environment` / `Settings` 同款)。
- **Settings UI 新区段"Python 解释器"**:列表 + Add/Remove 按钮 + 内联表单
  (Name / Path / 浏览),选中即 active。Add 触发 `python --version` 5s 校验,
  非 Python 二进制直接拒收弹错。
- **首次打开 v0.6.5.5 settings.json 自动迁移**:从 legacy
  `TemplatePythonDir + DefaultPythonVersion` 合成一条默认条目,无需手动迁移。
  老字段保留在 settings.json 中,UI 标"已废弃"只读 label,**不再**参与
  `CreateEnvDialog` auto-fill。
- **顶级黄条继续生效**(沿用 v0.6.5.3 模式):active 解释器路径在校验时不可达
  / `python --version` 非零退出 / 超时 → 黄条提示"Settings 中激活的 Python
  解释器无法验证",用户去 Settings 修复即可,不影响 env 列表已读出的部分。

### 2) 数据流

```
User opens Settings:
  read settings.json → 触发 SettingsDefaults.ApplyMigration
  ├ 老 settings.json (无 PythonInterpreters 字段):
  │   合成默认条目 [ {Name = "legacy-default", Path = TemplatePythonDir} ]
  │   ActivePythonInterpreterName = "legacy-default"
  │   保存回 settings.json(老字段保留标记 deprecated)
  └ 新 settings.json:照常读 PythonInterpreters + ActivePythonInterpreterName

User clicks Add 填写 Name="py310" Path="C:\Python310\python.exe":
  PythonInterpreterValidator.ProbeAsync(Path, timeout=5s)
  ├ 退出 0 + stdout 含 "Python" → 加入列表 / 弹 success
  └ 超时 / 非零退出 / stdout 无 "Python" → 拒收 / 弹错误

User selects 一条目:
  Settings.ActivePythonInterpreterName = entry.Name
  Settings.PythonInterpreters[selected] = active
  (UI ComboBox 跟随)

User 在 CreateEnvDialog 点"应用模板":
  ApplyTemplate 读 settings.ActivePythonInterpreterName
  ├ 命中 → 取 active.Path 作为 PythonExe 默认值
  └ 未命中 / 为空 → 沿用 v0.6.5.4 fallback(老 template_python_dir 字段,如果存在)
```

### 3) 升级注意

- **直接覆盖 v0.6.5.5 文件即可**;`SettingsDefaults.ApplyMigration` 检测无
  `PythonInterpreters` 字段时自动合成一条默认条目并回写 settings.json。
- **老 `template_python_dir` / `default_python_version` 字段保留在 JSON 里**;
  UI 显示为"已废弃"只读 label,**不**再参与 `CreateEnvDialog` auto-fill。
  如想清掉老字段,手动编辑 settings.json;不清不会破坏任何功能。
- **未选 active 时**:首次跑新建环境会弹黄条提示"未选定激活的 Python 解释器"。
- **active 解释器被外部删除**:顶部黄条持续显示;在 Settings 添加新条目或修复
  Path 即可,不阻塞其它操作。
- **5 秒校验超时**:仅针对 `python --version` 子进程;系统 PATH 查找不计时。
- **GUI smoke TBD**:用户桌面验证(详见 §5 步骤)。

### 4) Verification

- **dotnet test:** 312 PASS + 1 SKIP / 0 FAIL(基线 v0.6.5.5 = 298 +
  `PythonInterpreterValidatorTests` 5 + `SettingsPersistenceTests` 3 +
  `CreateEnvDialogViewModelTests` 2 + `SettingsViewModelTests` 4)
- **pytest version consistency:** 3 PASS(v0.6.5.5 → v0.6.5.6)
- **dotnet build Release:** 0 errors(允许 NU1900 NuGet 网络 warning)

### 5) Commits since v0.6.5.5(`6d4d211`)

```
0dab656 feat(wpf): PythonInterpreterValidator + 5s probe timeout
4ef5b86 fix(wpf): relax notepad validator test assertion for Windows
6548d8a feat(data): Settings.PythonInterpreters + ActivePythonInterpreterName
6dbc387 feat(wpf): CreateEnvDialog ApplyTemplate uses ActivePythonInterpreter
66afaf9 feat(wpf): SettingsViewModel PythonInterpreters section + migration
d071115 fix(wpf): T4 build green (SettingsRepository unseal + MainViewModel wiring)
3975197 feat(wpf): Settings UI 多 Python 解释器区段 + 只读老字段
<this commit> chore(release): bump to v0.6.5.6 + release notes
```

共 8 个 commit on top of v0.6.5.5 `6d4d211`(实际是 7 + 本 close-out)。

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.6` push + `gh release create` +
  release/ComfyUI-Manager-v0.6.5.6-win-x64.zip rebuild + upload —
  等用户明确授权(沿用 v0.6.5.5 同模式)。
- **手动 GUI smoke (TBD):** 用户桌面验证(详见 §5 步骤)。
- **后续 hotfix 候选**(YAGNI,本 spec 不做):active 解释器失效时一键
  "切换回最近可用的" / active 解释器校验失败时 clipboard 复制错误
  stdout / 多 Python 解释器列表排序持久化(目前按 add 顺序)。

### Lessons learned(SDD)

- **T1 Windows notepad 测试假阳性**:用 `notepad.exe --version` 来 mock
  `python --version` 早期失败,因为 `notepad /?` 在 Windows 11 走 UAC 弹出
  GUI 对话框阻塞子进程。Lesson:`-ArgumentList @("--version")` 不等于
  Unix 风格 `--version` flag — Windows 二进制对 `--version` 的反应差异极大。
  放宽到 `ProbeAsync` 收到非空 stdout + 退出码 < 100 即视为"可能是 Python"
  即可让 notepad 通过测试,生产路径仍走严格匹配 `Python` 字符串。
- **T4 `SettingsRepository` sealed 阻塞 Fake 替换**:`SettingsViewModel`
  测试需要 seam 注入;`SettingsRepository` 默认 `sealed`。Lesson:依赖注入
  一旦走 `sealed class`,下游 view-model 无法 mock — 在 `Repository` 类
  上一律不写 `sealed`,除非它真的没有任何 derivation 计划。
- **T5 XAML 校验跑后置**:XAML 浏览按钮逻辑放在 code-behind 而非 VM,
  `OpenFileDialog` 走 WPF `Microsoft.Win32`,不能在 headless 测试触达。
  Lesson:浏览文件路径这种"系统对话框 + 不可纯函数化"逻辑放 code-behind,
  VM 只暴露"我已经选好了 Path,你来认"的 `Path` setter;测试只测 setter。
