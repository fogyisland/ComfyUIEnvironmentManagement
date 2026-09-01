using System.ComponentModel;
using ComfyUI.Manager.Models;
using Xunit;

namespace ComfyUI.Manager.Tests.Models;

/// <summary>
/// v1.0.0.x (2026-09-01) T25 hotfix:锁 Environment 的 INotifyPropertyChanged 行为。
///
/// 背景:启停按钮 ready gate 需要在 probe 完后从 disabled → enabled 切换,
/// 普通 POCO property 设值不通知 UI,XAML 看不到变化。Environment :
/// INotifyPropertyChanged + 2 UI-only 字段(StartStopButtonEnabled /
/// StartStopButtonTooltip)用 backed property + setter raise 通知。
///
/// 测试矩阵:
/// <list type="bullet">
///   <item>2 字段 set 新值 → PropertyChanged 触发,propertyName 正确</item>
///   <item>2 字段 set 同值 → 不触发(防抖动)</item>
///   <item>持久化字段(Id/Name/RootPath/Status) 设值 → 不触发 INPC(走 DB Upsert)</item>
/// </list>
/// </summary>
public class EnvironmentPropertyChangedTests
{
    private static (string? name, int count) CaptureSet(Environment env, System.Action setter)
    {
        string? captured = null;
        int count = 0;
        env.PropertyChanged += (_, e) =>
        {
            captured = e.PropertyName;
            count++;
        };
        setter();
        return (captured, count);
    }

    [Fact]
    public void StartStopButtonEnabled_Set_RaisesPropertyChanged()
    {
        var env = new Environment();
        var (name, count) = CaptureSet(env, () => env.StartStopButtonEnabled = false);

        Assert.Equal(nameof(Environment.StartStopButtonEnabled), name);
        Assert.Equal(1, count);
        Assert.False(env.StartStopButtonEnabled);
    }

    [Fact]
    public void StartStopButtonTooltip_Set_RaisesPropertyChanged()
    {
        var env = new Environment();
        var (name, count) = CaptureSet(env, () => env.StartStopButtonTooltip = "缺:基础环境");

        Assert.Equal(nameof(Environment.StartStopButtonTooltip), name);
        Assert.Equal(1, count);
        Assert.Equal("缺:基础环境", env.StartStopButtonTooltip);
    }

    [Fact]
    public void SameValue_DoesNotRaise_NoSpam()
    {
        // 防抖动:设同值不应该触发 PropertyChanged(避免 XAML 死循环)
        var env = new Environment { StartStopButtonEnabled = true };
        var (name, count) = CaptureSet(env, () => env.StartStopButtonEnabled = true);

        Assert.Null(name);
        Assert.Equal(0, count);
    }

    [Fact]
    public void PersistedFields_DoNotRaisePropertyChanged()
    {
        // 持久化字段(Id/Name/RootPath/Status)走 DB Upsert 路径,
        // 不需要 INPC —— 设值不该触发 PropertyChanged
        var env = new Environment();
        var (_, count) = CaptureSet(env, () =>
        {
            env.Id = "env-test";
            env.Name = "test";
            env.RootPath = "C:\\test";
            env.Status = "running";
        });

        Assert.Equal(0, count);
    }

    [Fact]
    public void Defaults_AreCorrect_AfterINPCRefactor()
    {
        // backed property 默认值必须跟原来 plain auto-property 一致
        var env = new Environment();
        Assert.True(env.StartStopButtonEnabled);
        Assert.Equal("", env.StartStopButtonTooltip);
    }

    [Fact]
    public void IsBaseEnvInstalled_PlainProperty_DoesNotRaise()
    {
        // IsBaseEnvInstalled 是 T19 老 plain property(Load() 直接写,不通知 UI
        // 即可 —— Load 末尾启停按钮 enabled 重算用最新值)。T25 不改这个。
        var env = new Environment();
        var (_, count) = CaptureSet(env, () => env.IsBaseEnvInstalled = true);

        Assert.Equal(0, count);
        Assert.True(env.IsBaseEnvInstalled);
    }
}