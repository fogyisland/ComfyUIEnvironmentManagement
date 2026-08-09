// v0.6.9.3 T3:验证齿轮按钮及主题资源可在 STA 中完成布局。
// v0.6.9.3 final-review fix:统一走 WpfTestResources(避免 3 个 load test 各自
// new Application 抛 "不能在同一 AppDomain 中创建多个")。
using System;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Controls;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

public class GearIconButtonLoadTests
{
    [Fact]
    public void GearIconButton_Instantiation_DoesNotThrow()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
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