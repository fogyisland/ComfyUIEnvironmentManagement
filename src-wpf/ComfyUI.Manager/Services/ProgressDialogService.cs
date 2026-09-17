using System;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:进度对话框服务接口(项目此前无此抽象,本 task 落地)。
/// ReloadHashAsync / RefreshMetadataAsync 等长 IO 操作通过 <c>ShowIndeterminate</c> 弹
/// indeterminate progress;返回的 <see cref="IDisposable"/> 在 using 块结束时自动关闭 dialog。
/// 测试用 Moq/NSubstitute mock 本接口,避免真开窗。
/// </summary>
public interface IProgressDialogService
{
    /// <summary>弹出 indeterminate 进度对话框,显示 <paramref name="message"/>;
    /// Dispose 时关闭。返回的 handle 让 caller 用 <c>using var _ = ...</c> 自动释放。</summary>
    IDisposable ShowIndeterminate(string message);
}

/// <summary>
/// v1.0.0.x (2026-09-17) T47:IProgressDialogService 最小实现 —— Debug 输出占位,
/// SemaphoreSlim 串行化避免重入(同一时间最多 1 个 progress 显示,后续 ShowIndeterminate
/// 立即拿 handle、上一句 Dispose 时才真正触发;若 caller Dispose 顺序正常 = 1 个 dialog 走完)。
/// 真 modal progress window 留给后续 task 落地。
/// </summary>
public sealed class ProgressDialogService : IProgressDialogService
{
    private readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

    public IDisposable ShowIndeterminate(string message)
    {
        _gate.Wait();
        try
        {
            System.Diagnostics.Debug.WriteLine($"[Progress] {message}");
        }
        catch
        {
            // 占位输出失败不影响 handle 返回
        }
        return new DisposeHandle(_gate);
    }

    private sealed class DisposeHandle : IDisposable
    {
        private readonly System.Threading.SemaphoreSlim _gate;
        private bool _disposed;

        public DisposeHandle(System.Threading.SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _gate.Release(); } catch { /* 幂等 release,容错吞 */ }
        }
    }
}
