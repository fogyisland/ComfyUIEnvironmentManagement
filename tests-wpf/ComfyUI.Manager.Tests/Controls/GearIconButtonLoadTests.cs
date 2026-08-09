// v0.6.9.3 T3:验证齿轮按钮及主题资源可在 STA 中完成布局。
using System;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Controls;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

public class GearIconButtonLoadTests
{
    private static bool _resourcesLoaded;
    private static readonly object _lock = new();

    private static void EnsureApplicationResources()
    {
        if (_resourcesLoaded) return;
        lock (_lock)
        {
            if (_resourcesLoaded) return;
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            var theme = new ResourceDictionary
            {
                Source = new Uri(
                    "/ComfyUI.Manager;component/Resources/Theme.xaml",
                    UriKind.Relative)
            };
            var palette = new ResourceDictionary
            {
                Source = new Uri(
                    "/ComfyUI.Manager;component/Themes/Palette.Dark.xaml",
                    UriKind.Relative)
            };
            Application.Current.Resources.MergedDictionaries.Add(theme);
            Application.Current.Resources.MergedDictionaries.Add(palette);
            _resourcesLoaded = true;
        }
    }

    [Fact]
    public void GearIconButton_Instantiation_DoesNotThrow()
    {
        EnsureApplicationResources();
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                var button = new GearIconButton();
                button.Measure(new Size(32, 32));
                button.Arrange(new Rect(0, 0, 32, 32));
                button.UpdateLayout();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is not null)
        {
            throw new Exception(
                $"GearIconButton load failed: {caught.GetType().FullName}: {caught.Message}\n" +
                $"--- InnerException ---\n{caught.InnerException}\n" +
                $"--- StackTrace ---\n{caught.StackTrace}",
                caught);
        }
    }
}
