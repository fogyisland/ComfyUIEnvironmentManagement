using System.Collections.Generic;
using System.ComponentModel;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class CreateStepViewModelTests
{
    [Fact]
    public void Glyph_IsPending_WhenInitiallyConstructed()
    {
        var step = new CreateStepViewModel("测试步骤");

        Assert.Equal("○", step.Glyph);
        Assert.Equal(CreateStepStatus.Pending, step.Status);
        Assert.Null(step.Detail);
        Assert.Equal("测试步骤", step.Name);
    }

    [Fact]
    public void Status_RaisesPropertyChangedFor_Glyph()
    {
        // WPF DataTrigger 用 Glyph 切换图标/颜色,Status setter 必须触发 Glyph INPC
        var step = new CreateStepViewModel("测试");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)step).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        step.Status = CreateStepStatus.Running;

        Assert.Contains("Status", raised);
        Assert.Contains(nameof(step.Glyph), raised);
    }

    [Theory]
    [InlineData(CreateStepStatus.Pending, "○")]
    [InlineData(CreateStepStatus.Running, "●")]
    [InlineData(CreateStepStatus.Done, "✓")]
    [InlineData(CreateStepStatus.Failed, "✗")]
    public void Glyph_MapsEachStatus(CreateStepStatus status, string expected)
    {
        var step = new CreateStepViewModel("x") { Status = status };

        Assert.Equal(expected, step.Glyph);
    }

    [Fact]
    public void Detail_SetterIsObservable()
    {
        var step = new CreateStepViewModel("x");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)step).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        step.Detail = "path: /foo/bar";

        Assert.Equal("path: /foo/bar", step.Detail);
        Assert.Contains(nameof(step.Detail), raised);
    }
}
