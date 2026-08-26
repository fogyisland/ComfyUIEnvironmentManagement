using System;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// "ComfyUI 课程" 顶级 dropdown 对话框 VM 测试 — v1.0.0 拆分新增。
/// 验证 5 个核心字段非空 + Close 命令触发 RequestClose 事件。
/// 课程名从 resx (About_Course_*) 来,Title/Hint 走硬编码中文 —
/// 跟 v0.6.15.5 起关于对话框内嵌课程的字段完全对等,留作回归测试。
/// </summary>
public class ComfyUICoursesViewModelTests
{
    [Fact]
    public void Ctor_LoadsTitleHintNonEmpty()
    {
        var vm = new ComfyUICoursesViewModel();
        Assert.False(string.IsNullOrEmpty(vm.Title));
        Assert.False(string.IsNullOrEmpty(vm.Hint));
    }

    [Fact]
    public void Ctor_LoadsAllCourseNames()
    {
        var vm = new ComfyUICoursesViewModel();
        Assert.False(string.IsNullOrEmpty(vm.CoursesHeader));
        Assert.False(string.IsNullOrEmpty(vm.Course51CTO));
        Assert.False(string.IsNullOrEmpty(vm.CourseShenYeCG));
        Assert.False(string.IsNullOrEmpty(vm.CourseYihuu));
        Assert.False(string.IsNullOrEmpty(vm.CourseUdemy));
    }

    [Fact]
    public void CloseCommand_Execute_FiresRequestClose()
    {
        var vm = new ComfyUICoursesViewModel();
        var fired = false;
        vm.RequestClose += (_, _) => fired = true;
        vm.CloseCommand.Execute(null);
        Assert.True(fired);
    }
}
