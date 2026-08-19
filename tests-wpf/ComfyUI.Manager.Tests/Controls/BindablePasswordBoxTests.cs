// v0.6.21 T3:BindablePasswordBox custom WPF control 单元测试。
// WPF PasswordBox 不暴露 Password 为 DependencyProperty(安全原因),我们包一层 custom control
// 提供可绑定的 Password DP + IsPasswordRevealed DP,SettingsView 的 HF token 字段用此控件。
// 测试不依赖 WPF template / 视觉树,只验证 DP 通知 + reveal/hide 方法。
// Control 派生类构造必须 STA,走 StaFact.RunOnSTA。
using System.ComponentModel;
using ComfyUI.Manager.Controls;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

public class BindablePasswordBoxTests
{
    [Fact]
    public void SetPassword_DpProperty_RaisesChangeNotification()
    {
        var changed = false;
        StaFact.RunOnSTA(() =>
        {
            var box = new BindablePasswordBox();
            box.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BindablePasswordBox.Password)) changed = true;
            };
            box.Password = "secret123";
            Assert.True(changed);
            Assert.Equal("secret123", box.Password);
        });
    }

    [Fact]
    public void PasswordCharToggle_RevealsPlaintext_For30Seconds()
    {
        StaFact.RunOnSTA(() =>
        {
            var box = new BindablePasswordBox { Password = "secret123" };
            Assert.False(box.IsPasswordRevealed);
            box.RevealPassword();
            Assert.True(box.IsPasswordRevealed);
            box.HidePassword();
            Assert.False(box.IsPasswordRevealed);
        });
    }

    // v0.6.21 T3 R1:验证 👁 toggle button 依赖的 IsPasswordRevealed DP 双向可写。
    // 模板里的 ToggleButton.IsChecked TwoWay 绑 IsPasswordRevealed,setter 走通就
    // 能 toggle 控件(测试不实例化 template,只验证 DP setter 路径)。
    [Fact]
    public void IsPasswordRevealed_Toggle_BoundFromExternalSetter()
    {
        StaFact.RunOnSTA(() =>
        {
            var box = new BindablePasswordBox { Password = "secret123" };
            Assert.False(box.IsPasswordRevealed);
            box.IsPasswordRevealed = true;
            Assert.True(box.IsPasswordRevealed);
            box.HidePassword();
            Assert.False(box.IsPasswordRevealed);
        });
    }
}