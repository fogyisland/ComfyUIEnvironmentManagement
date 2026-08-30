using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-08-30):env-create step 7.6 — 写 LTX-2 wrapper .bat。
/// ProcessLauncher 不动;EntryScript 直接指向 wrapper,uv 路径用 <c>%~dp0</c> 相对解析
/// → env 可搬到任意机器 + env 改路径不需要重新生成 wrapper。
///
/// 生成两个 wrapper:
/// - <c>run-ltx2-distilled.bat</c> → <c>python -m ltx_pipelines.distilled</c>(quick start 默认)
/// - <c>run-ltx2-dfr.bat</c> → <c>python -m ltx_pipelines.dfr_pipeline</c>(生产质量)
/// </summary>
public sealed class Ltx2WrapperGenerator : EnvCreatorService.ILtx2WrapperGenerator
{
    private const string WrapperTemplate = @"@echo off
""%~dp0tools\uv\uv.exe"" run python -m {0} %*
";

    private readonly string _envRoot;

    public Ltx2WrapperGenerator(string envRoot)
    {
        _envRoot = envRoot ?? throw new ArgumentNullException(nameof(envRoot));
    }

    public async Task GenerateAsync(CancellationToken ct = default)
    {
        await WriteWrapperAsync("run-ltx2-distilled.bat", "ltx_pipelines.distilled", ct).ConfigureAwait(false);
        await WriteWrapperAsync("run-ltx2-dfr.bat", "ltx_pipelines.dfr_pipeline", ct).ConfigureAwait(false);
    }

    private async Task WriteWrapperAsync(string fileName, string modulePath, CancellationToken ct)
    {
        var path = Path.Combine(_envRoot, fileName);
        var content = string.Format(WrapperTemplate, modulePath);
        // ASCII content,但 UTF-8 no BOM 写 bat 在 Windows 下兼容 cmd.exe / Explorer 都 OK
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);
    }
}
