using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.ViewModels;

public class NodeInstallDiffWarningViewModel : ViewModelBase
{
    private bool _proceed;

    public NodeInstallDiffWarningViewModel(
        NodeInstallDiffReport report, string nodePackage, string envName)
    {
        NodePackage = nodePackage;
        EnvName = envName;
        Warnings = new ObservableCollection<DiffEntry>(report.Warnings);
        CancelCommand = new RelayCommand(_ => { Proceed = false; CloseRequested?.Invoke(); });
        ProceedCommand = new RelayCommand(_ => { Proceed = true; CloseRequested?.Invoke(); });
    }

    public event Action? CloseRequested;

    public string NodePackage { get; }
    public string EnvName { get; }
    public ObservableCollection<DiffEntry> Warnings { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ProceedCommand { get; }

    /// <summary>
    /// 调方 modal 关闭后读这个值 — true = 用户仍然安装,false = 用户取消。
    /// </summary>
    public bool Proceed
    {
        get => _proceed;
        private set => SetField(ref _proceed, value);
    }
}