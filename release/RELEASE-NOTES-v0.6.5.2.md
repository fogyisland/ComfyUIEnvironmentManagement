## v0.6.5.2 — 修 v0.6.5.1:live fetch 被 bundled 文件 shadow

**v0.6.5.2 是 v0.6.5.1 的紧急 hotfix。v0.6.5.1 的"基础环境"页面
依然显示硬编码 `PyTorch 2.1` profile,live fetch 完全没触发 —
根因是 csproj 把 v0.6.5 的 `base_env_profiles.json`(5 个 hardcoded
profile)作为 bundled 资源随 exe 一起打包,loader 启动时优先读这个
文件就直接返回了,根本走不到 `GetLiveDefaultsAsync` 分支。**

---

### 1) 根因

`ComfyUI.Manager.csproj` 里有这一段:

```xml
<None Include="base_env_profiles.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

→ build 时 `base_env_profiles.json` 复制到 `<exe-dir>/`
→ `App.xaml.cs:76` 把 `projectRoot`(= `<exe-dir>`)作为
  `BaseEnvProfileLoader` 的 `appDataDir` 第一参
→ `LoadAsync` 在 `<exe-dir>/base_env_profiles.json` 找到 bundled 文件
  → 直接 `JsonSerializer.Deserialize` 返回 5 个 v0.6.5 hardcoded
  profile → **永远不调用 `GetLiveDefaultsAsync`**

虽然 `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json` 里
`Stable="2.13.0"` 写对了,但 loader 根本没读 cache。

### 2) 修复

**v0.6.5.2 改动:** 删掉 csproj 那一段 `<None Include="base_env_profiles.json">`,
不再 ship bundled 文件。Loader 的"文件缺失 → live fetch"分支终于能被走到。

```diff
   <ItemGroup>
-    <None Include="base_env_profiles.json">
-      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
-    </None>
-  </ItemGroup>
-  <ItemGroup>
     <InternalsVisibleTo Include="ComfyUI.Manager.Tests" />
   </ItemGroup>
```

**用户 override 路径不变:** v0.6.5 时期在 `<exe-dir>/base_env_profiles.json`
里手动编辑的 profile 仍生效 — loader 优先读 `<exe-dir>/base_env_profiles.json`,
命中就用,没命中才走 live fetch。power user 的配置无损。

### 3) 升级注意

- 直接覆盖 v0.6.5.1 文件即可。
- 升级前 v0.6.5.1 已经写过 `%APPDATA%/ComfyUI-Manager/pytorch_versions_cache.json`
  (含 `Stable="2.13.0"` 等) — cache 仍有效,首次启动直接命中,
  无需重打 pytorch.org。
- v0.6.5 时期在 `<exe-dir>/base_env_profiles.json` 的手动配置仍然生效
  (loader 优先读 exe-dir 文件)。
- **v0.6.5.1 用户必升** — v0.6.5.1 的 BED 页永远显示 v0.6.5 hardcoded
  profile,live fetch 是 dead code。

---

### 4) Verification

- **pytest version consistency:** 3 PASS(0.6.5.1 → 0.6.5.2)
- **dotnet test WPF:** 210 PASS + 1 SKIP / 0 FAIL
- **dotnet build Release:** 0 warnings, 0 errors
- **staging dir 验证:** `release/staging/ComfyUI Manager/base_env_profiles.json`
  不存在(已删)
- **Manual GUI smoke:** exe 启动后,基础环境页显示 6 个 profile,
  `Stable="2.13.0"`,nightly cu126(用户桌面验证中)

---

### 5) Commits since v0.6.5.1(`7879d06`)

```
(uncommitted at session boundary) — csproj + 5 version bumps → v0.6.5.2
```

---

### 已知 carry-over / 未做事项

- **未在本 session 完成:** tag `v0.6.5.2` push + `gh release create` —
  由用户在下一 session 续做(详见 `project_hotfix_pytorch_live.md` 的
  Resume 路径)。
- **LoadAsync 多位置查找** 未实现:当前只查 `<exe-dir>`,不查
  `<%APPDATA%/ComfyUI-Manager>`。如果用户想从 v0.6.5 override 路径
  迁到 appData 路径,需手动复制文件。
- **v0.6.5.1 GitHub release 保留** — 不 amend / 不重发(可能给已下载
  v0.6.5.1 的用户造成混淆)。v0.6.5.2 发布后自动 demote v0.6.5.1 为非
  Latest。

---

### Lessons learned(SDD)

- **HTML/spec 假设永远要 verify against real sample** — T2 spec 假设的
  pytorch.org nested 结构跟实际 flat 不符,被 T2.5 smoke 抓到修。
- **Bundled "default" 文件会 shadow 新功能** — v0.6.5 ship 的
  `base_env_profiles.json` 是为了让老用户看到东西,但 v0.6.5.1 加了
  live fetch 后它成了死代码。spec/plan 评审时应该问:"ship 这个文件
  跟新 feature 的 fallback 路径是否冲突?"