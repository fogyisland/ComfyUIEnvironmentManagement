using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.22 T5:inline status panel for ComfyUI template update — mirrors
/// RequirementsStatusViewModel pattern (v0.6.5.12 hotfix).
///
/// 3-state visibility: !userHidden &amp;&amp; (IsBusy || LogLines.Count &gt; 0 || HasError).
/// Unlike RequirementsStatusViewModel, the host (EnvironmentListViewModel)
/// drives the work via RunAsync(workFunc) so the service's progress strings
/// flow into LogLines without the VM knowing about GitRunner / wipe loop.
/// </summary>
public sealed class TemplateUpdateStatusViewModel : ViewModelBase
{
    private bool _userHidden;
    private bool _isBusy;
    private string? _error;

    public string Title { get; set; } = "模板更新状态";
    public string StatusText { get; set; } = "";
    public ObservableCollection<string> LogLines { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsVisible));
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (_error == value) return;
            _error = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsVisible));
            RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);
    public bool IsVisible => !_userHidden && (IsBusy || LogLines.Count > 0 || HasError);

    /// <summary>User clicked ✕ — hide panel but keep log/state for re-open.</summary>
    public RelayCommand ClearCommand { get; }

    public TemplateUpdateStatusViewModel()
    {
        ClearCommand = new RelayCommand(_ => Clear());
    }

    /// <summary>User-initiated hide via ✕ button.</summary>
    public void Clear()
    {
        _userHidden = true;
        RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>Reset for a fresh run — show panel + clear state.</summary>
    public void Reset()
    {
        _userHidden = false;
        IsBusy = false;
        LogLines.Clear();
        Error = null;
        StatusText = "";
        RaisePropertyChanged(nameof(IsVisible));
    }

    /// <summary>
    /// Run <paramref name="work"/> with a Progress&lt;string&gt; hooked to LogLines.
    /// Captures UI SynchronizationContext via Progress&lt;T&gt; ctor (same
    /// pattern as EnvStartStatusViewModel + RequirementsStatusViewModel).
    /// Exceptions surface as Error; never re-thrown.
    /// </summary>
    public async Task RunAsync(Func<System.IProgress<string>?, Task> work)
    {
        Reset();
        var progress = new Progress<string>(line => LogLines.Add(line));
        IsBusy = true;
        try
        {
            await work(progress);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}