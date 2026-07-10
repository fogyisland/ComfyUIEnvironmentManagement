using System;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.ViewModels;

public class ConfirmDialogViewModel : ViewModelBase
{
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event Action<bool>? Closed;

    public ConfirmDialogViewModel(string message,
        string confirmText = "确认", string cancelText = "取消")
    {
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        ConfirmCommand = new RelayCommand(_ => Closed?.Invoke(true));
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(false));
    }
}