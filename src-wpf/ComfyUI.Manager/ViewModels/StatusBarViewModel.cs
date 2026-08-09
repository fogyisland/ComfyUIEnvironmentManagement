using System;
using System.ComponentModel;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// v0.6.9.3 T2:显示当前主页面名称与应用版本，并跟随主导航变化同步。
/// </summary>
public sealed class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _mainViewModel;
    private string _currentSectionName;

    public StatusBarViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _currentSectionName = MainSectionNameProvider.GetName(_mainViewModel.CurrentSection);
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
    }

    public string CurrentSectionName
    {
        get => _currentSectionName;
        private set => SetField(ref _currentSectionName, value);
    }

    public string Version { get; } = AppVersionInfo.Current;

    public void Dispose()
    {
        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentSection))
        {
            CurrentSectionName = MainSectionNameProvider.GetName(_mainViewModel.CurrentSection);
        }
    }
}
