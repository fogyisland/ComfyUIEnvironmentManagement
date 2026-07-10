using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

public class InstallDialogViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    public CatalogEntry Entry { get; }
    public ObservableCollection<Environment> Environments { get; } = new();
    public RelayCommand InstallCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;
    public event Func<string, string, Task>? ErrorOccurred;

    public InstallDialogViewModel(ApiClient api, CatalogEntry entry)
    {
        _api = api; Entry = entry;
        InstallCommand = new RelayCommand(
            async _ => await InstallAsync(),
            _ => SelectedEnv is not null && !Busy);
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
        _ = LoadEnvsAsync();
    }

    private Environment? _selectedEnv;
    public Environment? SelectedEnv { get => _selectedEnv; set => SetField(ref _selectedEnv, value); }

    private bool _busy;
    public bool Busy { get => _busy; set { if (SetField(ref _busy, value)) InstallCommand.RaiseCanExecuteChanged(); } }

    private string? _progress;
    public string? Progress { get => _progress; set => SetField(ref _progress, value); }

    private async Task LoadEnvsAsync()
    {
        var r = await _api.PostAsync<List<Environment>>(
            "env/list", new { });
        if (r.Ok && r.Value is not null)
        {
            foreach (var e in r.Value) Environments.Add(e);
            if (Environments.Count > 0) SelectedEnv = Environments[0];
        }
    }

    private async Task InstallAsync()
    {
        if (SelectedEnv is null) return;
        Busy = true;
        Progress = "安装中...";
        try
        {
            await _api.PostAsync<object>("node/install-from-catalog",
                new { package = Entry.Id, target_env_id = SelectedEnv.Id });
            Progress = "完成";
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Progress = $"失败: {ex.Message}";
            if (ErrorOccurred is not null)
                await ErrorOccurred("INSTALL_FAILED", ex.Message);
        }
        finally { Busy = false; }
    }
}
