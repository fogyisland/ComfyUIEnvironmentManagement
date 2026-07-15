using System;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.ViewModels;

public class CreateEnvDialogViewModel : ViewModelBase
{
    private readonly EnvCreatorService _creator;
    private readonly Action<Models.Environment?>? _onResult;

    public CreateEnvDialogViewModel(
        EnvCreatorService creator,
        Action<Models.Environment?>? onResult = null)
    {
        _creator = creator;
        _onResult = onResult;
        CreateCommand = new RelayCommand(
            async _ => await CreateAsync(),
            _ => CanCreate());
        CancelCommand = new RelayCommand(_ => Closed?.Invoke(null));
    }

    public event Action<Models.Environment?>? Closed;

    public System.Collections.Generic.List<string> LayoutOptions { get; } =
        new() { "shared", "independent" };

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _layout = "shared";
    public string Layout
    {
        get => _layout;
        set { _layout = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _pythonExe = "";
    public string PythonExe
    {
        get => _pythonExe;
        set { _pythonExe = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _comfyuiSource = "";
    public string ComfyuiSource
    {
        get => _comfyuiSource;
        set { _comfyuiSource = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string _port = "";
    public string Port
    {
        get => _port;
        set { _port = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); RaiseCommandsChanged(); }
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; RaisePropertyChanged(); }
    }

    public RelayCommand CreateCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool CanCreate()
    {
        if (IsBusy) return false;
        if (string.IsNullOrWhiteSpace(Name)) return false;
        if (string.IsNullOrWhiteSpace(PythonExe)) return false;
        if (Layout == "shared" && string.IsNullOrWhiteSpace(ComfyuiSource)) return false;
        return true;
    }

    private async Task CreateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            int? port = null;
            if (int.TryParse(Port, out var p) && p > 0) port = p;

            var env = await _creator.CreateAsync(
                Name, Layout, PythonExe,
                string.IsNullOrWhiteSpace(ComfyuiSource) ? null : ComfyuiSource,
                port,
                CancellationToken.None);
            Closed?.Invoke(env);
        }
        catch (EnvCreatorService.CreateEnvException ex)
        {
            ErrorMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandsChanged()
    {
        CreateCommand.RaiseCanExecuteChanged();
    }
}
