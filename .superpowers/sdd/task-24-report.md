# Task T24 Report: `Tools.Ts2Resx` — Console Project

**Status:** DONE_WITH_CONCERNS
**Commit:** `dbf3630` — `feat(i18n): Ts2Resx console tool for .ts to .resx generation`

## What Was Done

Created the first C# / .NET 8 project in the repo: `src-wpf/Tools.Ts2Resx/`.

### Files Created (verbatim from brief + minimal compile-required fixes)

- `src-wpf/Tools.Ts2Resx/Tools.Ts2Resx.csproj` — net8.0 console exe (verbatim)
- `src-wpf/Tools.Ts2Resx/KeyDeriver.cs` — verbatim
- `src-wpf/Tools.Ts2Resx/TsParser.cs` — verbatim
- `src-wpf/Tools.Ts2Resx/ResxEmitter.cs` — verbatim
- `src-wpf/Tools.Ts2Resx/Program.cs` — verbatim
- `.gitignore` — appended `**/bin/` and `**/obj/` so build artifacts are excluded
- `src-wpf/ComfyUI.Manager/Resources/Strings.resx` — generated (112 entries, en_US defaults)
- `src-wpf/ComfyUI.Manager/Resources/Strings.zh-CN.resx` — generated (49 entries)

### Build & Test

- `dotnet build src-wpf/Tools.Ts2Resx/` — Build succeeded, 0 errors, 1 warning (CS8625 null literal on `Emit(path, null, ...)` — by design per brief signature)
- `dotnet run --project src-wpf/Tools.Ts2Resx -- --ts-dir app/qml/i18n/ --out src-wpf/ComfyUI.Manager/Resources/ --default-locale en_US --target-locales en_US,zh_CN`
  - Wrote `Strings.resx` (112 entries — default locale emits Source text as Value, per brief)
  - Wrote `Strings.zh-CN.resx` (49 entries — has non-empty translations)
- Manually inspected `Strings.resx` head: well-formed standard resx header (4 resheader blocks), Chinese keys sanitized correctly (e.g. `CatalogPage_节点目录` → Chinese chars preserved, `(` `)` `%` `,` dropped per Sanitize rules)

## Concerns (Brief Deviations)

The brief's verbatim code did not compile as-is. The following minimal, intent-preserving fixes were required:

1. **Missing `using` directives** (brief assumed implicit usings, but no `<ImplicitUsings>enable</ImplicitUsings>` is in the csproj):
   - `TsParser.cs`: added `using System;` (for `InvalidOperationException`)
   - `ResxEmitter.cs`: added `using System.Collections.Generic;` (for `IEnumerable<>`)
   - `Program.cs`: added `using System;`, `using System.Collections.Generic;`, `using System.IO;`, `using System.Linq;`
2. **Removed dead `using System.CommandLine;`** in `Program.cs` — the brief comment says "故意 NOT 引入" (intentionally NOT imported) but the using directive itself still tries to resolve and fails at compile. Removed per the brief's stated intent.
3. **Typo fix in `ResxEmitter.cs`**: brief line 25 wrote `WriteAttributeString("xmlns", "xsd", "i", null, ...)` (duplicated `"xsd"` and stray `"i"`). The WPF-recognised xsi namespace declaration should be `xmlns:xsi`. Fixed to `("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance")`. Standard resx files use both `xmlns:xsd` and `xmlns:xsi`.

All three deviations are mechanical, do not change behaviour or shape of generated resx, and were necessary for the project to build at all. Brief code intent is fully preserved.

## Notes

- .NET 10.0.102 SDK is installed (backwards-compatible with `net8.0` target).
- The brief's command uses `app/qml/i18n/` (correct — the actual .ts files live at `app/qml/i18n/comfyui_manager_{en_US,zh_CN}.ts`, NOT `app/i18n/`).
- 3 vanished translations (from M3 era) were correctly filtered out by `IsVanished` check.
- Vanished `data` lines absent from both resx outputs (correct — they should never be re-introduced by the tool).
- T25 (tests) and T26 (CI drift check) remain as separate tasks per the brief.

## Test Summary

- `.NET build OK, generated Strings.resx has 112 entries + Strings.zh-CN.resx has 49 entries`

## File Paths (absolute)

- `D:\ToolDevelop\ComfyUI\src-wpf\Tools.Ts2Resx\Tools.Ts2Resx.csproj`
- `D:\ToolDevelop\ComfyUI\src-wpf\Tools.Ts2Resx\Program.cs`
- `D:\ToolDevelop\ComfyUI\src-wpf\Tools.Ts2Resx\TsParser.cs`
- `D:\ToolDevelop\ComfyUI\src-wpf\Tools.Ts2Resx\ResxEmitter.cs`
- `D:\ToolDevelop\ComfyUI\src-wpf\Tools.Ts2Resx\KeyDeriver.cs`
- `D:\ToolDevelop\ComfyUI\src-wpf\ComfyUI.Manager\Resources\Strings.resx`
- `D:\ToolDevelop\ComfyUI\src-wpf\ComfyUI.Manager\Resources\Strings.zh-CN.resx`
- `D:\ToolDevelop\ComfyUI\.gitignore` (updated with `**/bin/` and `**/obj/`)
