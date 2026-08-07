using System;
using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

/// <summary>
/// v0.6.6 T3:Dialog 套壳测试。brief 未要求测试,这里补的是**运行期**风险:
/// XAML StaticResource 解析失败只在 InitializeComponent 抛,编译期发现不了
/// (v0.6.5.14 hotfix 就是漏注册 converter 导致的 XamlParseException)。
/// 注意:不测 <c>Show()</c> 的空 profiles 分支 — 那条路会弹真 MessageBox 阻塞 STA 线程。
/// </summary>
public class BaseEnvProfilePickerDialogTests
{
    private static BaseEnvProfile Profile(string cuda) =>
        new() { Id = $"torch==2.4.1+{cuda}", TorchVersion = "2.4.1", CudaVersion = cuda, CudaVariant = cuda };

    private static BaseEnvProfile[] ThreeProfiles() =>
        new[] { Profile("cu118"), Profile("cu121"), Profile("cu126") };

    [Fact]
    public void Ctor_ParsesXamlAndResolvesStaticResources()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = ThreeProfiles();
            // InitializeComponent 会解析 BackgroundBrush / MaterialButton;缺失即抛。
            var dlg = new BaseEnvProfilePickerDialog(profiles, null, PickerSelectionMode.Multi);
            Assert.NotNull(dlg);
        });
    }

    [Fact]
    public void Ctor_WithPreselection_PushesSelectionIntoListBox()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = ThreeProfiles();
            var dlg = new BaseEnvProfilePickerDialog(profiles, profiles[1], PickerSelectionMode.Multi);
            var picker = (BaseEnvProfilePickerView)dlg.FindName("Picker");
            Assert.Single(picker.ProfileListBox.SelectedItems);
            Assert.Contains(profiles[1], picker.ProfileListBox.SelectedItems.Cast<BaseEnvProfile>());
        });
    }

    [Fact]
    public void Ctor_SingleMode_AppliesSingleSelectionToListBox()
    {
        StaFact.RunOnSTA(() =>
        {
            var dlg = new BaseEnvProfilePickerDialog(ThreeProfiles(), null, PickerSelectionMode.Single);
            var picker = (BaseEnvProfilePickerView)dlg.FindName("Picker");
            Assert.Equal(
                System.Windows.Controls.SelectionMode.Single,
                picker.ProfileListBox.SelectionMode);
        });
    }

    /// <summary>
    /// T2 carry-forward:single-mode 下换选另一项不应崩,且最终只保留 1 个。
    /// Single 模式必须用 SelectedItem — WPF 禁止改 SelectedItems 集合。
    /// </summary>
    [Fact]
    public void SingleMode_SelectingSecondItem_KeepsExactlyOneSelected()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = ThreeProfiles();
            var dlg = new BaseEnvProfilePickerDialog(profiles, null, PickerSelectionMode.Single);
            var picker = (BaseEnvProfilePickerView)dlg.FindName("Picker");

            picker.ProfileListBox.SelectedItem = profiles[0];
            picker.ProfileListBox.SelectedItem = profiles[1];

            Assert.Single(picker.ProfileListBox.SelectedItems);
            Assert.Single(picker.ViewModel!.SelectedProfiles);
            Assert.Same(profiles[1], picker.ViewModel.SelectedProfiles[0]);
        });
    }

    /// <summary>
    /// T5(env-list)的真实调用形态:Single 模式 + 已装 profile 预选。
    /// ctor 先设 SelectionMode=Single 再设 SelectedProfiles,后者内部走
    /// SelectedItems.Clear()/Add() — WPF 在 Single 模式下禁止改该集合,
    /// 所以这条路一旦回归就是启动即崩,必须有测试钉住。
    /// </summary>
    [Fact]
    public void Ctor_SingleModeWithPreselection_DoesNotThrowAndSelectsIt()
    {
        StaFact.RunOnSTA(() =>
        {
            var profiles = ThreeProfiles();
            var dlg = new BaseEnvProfilePickerDialog(profiles, profiles[1], PickerSelectionMode.Single);
            var picker = (BaseEnvProfilePickerView)dlg.FindName("Picker");

            Assert.Single(picker.ProfileListBox.SelectedItems);
            Assert.Same(profiles[1], picker.ProfileListBox.SelectedItem);
        });
    }

    [Fact]
    public void Show_WithOverride_ReturnsOverrideResultWithoutOpeningWindow()
    {
        var profiles = ThreeProfiles();
        IReadOnlyList<BaseEnvProfile>? captured = null;
        BaseEnvProfile? capturedPreselect = null;
        PickerSelectionMode capturedMode = default;

        BaseEnvProfilePickerDialog.ShowOverride = (p, pre, mode) =>
        {
            captured = p;
            capturedPreselect = pre;
            capturedMode = mode;
            return new[] { p[2] };
        };
        try
        {
            var result = BaseEnvProfilePickerDialog.Show(profiles, profiles[0], PickerSelectionMode.Single);

            Assert.NotNull(result);
            Assert.Single(result!);
            Assert.Same(profiles[2], result![0]);
            Assert.Same(profiles, captured);
            Assert.Same(profiles[0], capturedPreselect);
            Assert.Equal(PickerSelectionMode.Single, capturedMode);
        }
        finally
        {
            BaseEnvProfilePickerDialog.ShowOverride = null;
        }
    }

    [Fact]
    public void Show_WithOverrideReturningNull_PropagatesCancel()
    {
        BaseEnvProfilePickerDialog.ShowOverride = (_, _, _) => null;
        try
        {
            var result = BaseEnvProfilePickerDialog.Show(ThreeProfiles(), null, PickerSelectionMode.Multi);
            Assert.Null(result);
        }
        finally
        {
            BaseEnvProfilePickerDialog.ShowOverride = null;
        }
    }
}
