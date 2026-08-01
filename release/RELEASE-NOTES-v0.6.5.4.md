## v0.6.5.4 — 新建环境:自动从设置带出 Python 解释器 + ComfyUI 模板

新建环境 dialog 之前 Python 解释器 / ComfyUI 源两个字段总是空的,即使 settings
里 `TemplatePythonDir` / `TemplateComfyuiDir` 早就配好了。v0.6.5.4 让 dialog
自动从 settings 拉常用模板路径,避免每次手填。

---

### 1) 新增功能

- **dialog 初次打开 auto-fill**:`PythonExe` / `ComfyuiSource` 自动从 settings
  拉,shared + independent 两个布局都生效。
- **"应用模板" 按钮**:Python / ComfyUI 两个字段行各加一个,用户改了
  settings 后可手动重新拉,不会自动覆盖用户已改的字段。
- **顶部黄色提示**:模板缺失(`PythonExe` 或 `ComfyuiSource` 不存在)时,dialog
  顶部显示"Python 模板 X.Y 未安装,请先在设置页下载"等提示。
- **`Settings.DefaultPythonVersion` 新字段**(默认 `"3.10"`):Settings 页
  加一行 ComboBox(可手填),auto-fill 时用来定位
  `<TemplatePythonDir>/<DefaultPythonVersion>/python.exe` 具体子目录。
- **`Layout` ComboBox 切换不重新 auto-fill**:只 dialog open + Apply 按钮
  触发,避免覆盖用户已手填的字段(决策 2)。

### 2) 数据流

```
User clicks "新建环境" in EnvListView
  ↓
MainViewModel.ShowEnvironmentsCommand
  ↓
CreateEnvDialog.Show(creator, settings, projectRoot)
  ↓
CreateEnvDialogViewModel ctor → ApplyTemplate()
  ↓
Fills PythonExe + ComfyuiSource + (optional) TemplateWarningMessage
  ↓
Dialog shown — user can edit or click "应用模板" to refetch
  ↓
User clicks "创建" → EnvCreatorService.CreateAsync (unchanged)
```

### 3) 升级注意

- **直接覆盖 v0.6.5.3 文件即可**。
- 老 `settings.json` 没 `default_python_version` 字段也兼容 — 反序列化时
  fallback 到 `"3.10"`。
- 不破坏现有手填 UX(用户改了字段不会被自动覆盖)。

### 4) Verification

- **dotnet test:** 285 PASS + 1 SKIP / 0 FAIL(基线 v0.6.5.3 = 273 +
  SettingsTests 3 + CreateEnvDialogViewModelTests 9 - 全量替换的旧测试)
- **pytest version consistency:** 3 PASS(v0.6.5.3 → v0.6.5.4)
- **dotnet build Release:** 0 warnings / 0 errors
- **手动 GUI smoke (TBD):** 启动 → 环境 → 新建 → 验证 PythonExe +
  ComfyuiSource 已 auto-fill;改 DefaultPythonVersion 到有子目录的版本,
  点"应用模板" 验证刷新;删 Python 模板子目录,重启,验证顶部黄色提示
  (注:staging exe 是旧 v0.6.5.3,新功能需未来 rebuild zip 后才能验证)

---

### 5) Commits since v0.6.5.3(`82cd854`)

```
1ac5b53 feat(wpf): thread projectRoot from App through MainViewModel to EnvList
0832e18 feat(wpf): thread projectRoot into CreateEnvDialog.Show + EnvListVM
e4a25c4 feat(wpf): CreateEnvDialog top warning + apply template buttons
4d7d475 feat(wpf): CreateEnvDialogViewModel ApplyTemplate + warnings
ea0495a feat(wpf): SettingsView adds DefaultPythonVersion picker
505d9e7 feat(wpf): Settings.DefaultPythonVersion + JSON round-trip tests
c2880bf docs(sdd): plan v0.6.5.4 — env create auto-fill from settings
27c2fc4 docs(sdd): specify env-create auto-fill from settings
```

---

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.4` push + `gh release create` —
  等用户明确授权(沿用 v0.6.5.3 同模式)。
- **手动 GUI smoke (TBD):** 用户桌面验证(详见 §4)。

---

### Lessons learned(SDD)

- **YAGNI > 抽 service**:决策 6,本 feature 只 1 个 caller(VM.ApplyTemplate),
  不需要抽 `EnvTemplateAutoFillService`,直接放 VM 里测试覆盖更直接。
- **决策记录防 YAGNI drift**:第 6 条"YAGNI"明确写在 spec §8 + plan G12,
  防止后续 PR 评审时把 service 抽回来。