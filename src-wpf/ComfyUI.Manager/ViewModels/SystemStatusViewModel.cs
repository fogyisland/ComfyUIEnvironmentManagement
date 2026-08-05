using System;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// SystemStatusViewModel:系统状态 tab 的 VM。
/// 持有 <see cref="SystemInfo"/> + 收集器;首次构造时自动 Refresh 一次
/// (MainViewModel.ShowSystemStatus 时调 ctor → 触发首次收集)。
/// 也提供 <see cref="RefreshCommand"/> 让用户手动重查。
///
/// 收集逻辑(nvidia-smi / nvcc)5s 超时,失败 → Gpus=[] / CudaVersion=null,
/// 不抛异常 → LastError 字段留空(nvidia-smi 没装是常态,不是错误)。
/// </summary>
public sealed class SystemStatusViewModel : ViewModelBase
{
    private readonly SystemInfoCollector _collector;

    private SystemInfo? _info;
    public SystemInfo? Info
    {
        get => _info;
        private set => SetField(ref _info, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set => SetField(ref _lastError, value);
    }

    public RelayCommand RefreshCommand { get; }

    public SystemStatusViewModel(SystemInfoCollector collector)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        RefreshCommand = new RelayCommand(
            execute: async _ => await RefreshAsync(),
            canExecute: _ => !IsLoading);
        // 自动刷新:构造完立即拉一次
        _ = RefreshAsync();
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;
        LastError = null;
        try
        {
            Info = await _collector.CollectAsync(ct);
        }
        catch (Exception ex)
        {
            LastError = $"收集失败:{ex.Message}";
            Info = null;
        }
        finally
        {
            IsLoading = false;
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }
}