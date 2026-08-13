using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class RateLimitBanner : UserControl
{
    public RateLimitBanner()
    {
        InitializeComponent();
    }

    private void OnDismissClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RateLimitBannerViewModel vm)
        {
            vm.DismissCommand.Execute(null);
        }
    }
}