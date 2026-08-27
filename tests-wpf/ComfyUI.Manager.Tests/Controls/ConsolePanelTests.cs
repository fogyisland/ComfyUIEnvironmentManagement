using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Threading;
using System.Windows;
using ComfyUI.Manager.Controls;
using Xunit;

namespace ComfyUI.Manager.Tests.Controls;

/// <summary>
/// v1.0.0.x #590:ConsolePanel DP + CloseRequested event 测试。
/// 必须在 STA thread 内跑(UserControl ctor 走 WPF stack,要求 STA apartment)。
/// ScrollViewer ScrollToEnd 行为留 GUI smoke。
/// </summary>
public class ConsolePanelTests
{
    private static T RunOnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestResources.EnsureLoaded(WpfTestResources.PaletteVariant.Dark);
                result = body();
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
                $"ConsolePanel test failed: {caught.GetType().FullName}: {caught.Message}",
                caught);
        }
        return result;
    }

    [Fact]
    public void NewPanel_TitleDefaultsToConsole()
    {
        var title = RunOnSta(() => new ConsolePanel().Title);
        Assert.Equal("Console", title);
    }

    [Fact]
    public void Title_CanBeSet()
    {
        var title = RunOnSta(() =>
        {
            var p = new ConsolePanel();
            p.Title = "Console (models)";
            return p.Title;
        });
        Assert.Equal("Console (models)", title);
    }

    [Fact]
    public void Lines_NullByDefault()
    {
        var lines = RunOnSta(() => new ConsolePanel().Lines);
        Assert.Null(lines);
    }

    [Fact]
    public void Lines_AcceptsObservableCollection()
    {
        var (panelLines, raised) = RunOnSta(() =>
        {
            var p = new ConsolePanel();
            var coll = new ObservableCollection<string> { "first" };
            p.Lines = coll;

            var raised = 0;
            ((INotifyCollectionChanged)coll).CollectionChanged += (_, _) => raised++;
            coll.Add("second");
            coll.Clear();

            return (p.Lines, raised);
        });

        Assert.IsType<ObservableCollection<string>>(panelLines);
        Assert.Equal(2, raised);  // Add + Clear
    }

    [Fact]
    public void Lines_AcceptsNonObservable()
    {
        var count = RunOnSta(() =>
        {
            var p = new ConsolePanel();
            p.Lines = new[] { "a", "b" };
            return ((System.Collections.ICollection)p.Lines!).Count;
        });
        Assert.Equal(2, count);
    }

    [Fact]
    public void Lines_ReplacingCollection_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var p = new ConsolePanel();
            var oldColl = new ObservableCollection<string>();
            var newColl = new ObservableCollection<string>();
            p.Lines = oldColl;
            p.Lines = newColl;
            oldColl.Add("orphan");  // 不应抛,旧 hook 已 unhook
            newColl.Add("hooked");
            return true;
        });
    }

    [Fact]
    public void Lines_SetToNull_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var p = new ConsolePanel();
            p.Lines = new ObservableCollection<string>();
            p.Lines = null;
            return true;
        });
    }

    [Fact]
    public void ConsoleCloseRequested_FiresOnClick()
    {
        var raised = RunOnSta(() =>
        {
            var p = new ConsolePanel();
            var count = 0;
            object? senderSeen = null;
            p.ConsoleCloseRequested += (s, _) => { count++; senderSeen = s; };

            var method = typeof(ConsolePanel).GetMethod(
                "OnCloseClicked",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method!.Invoke(p, new object?[] { p, null! });

            Assert.Equal(1, count);
            Assert.Same(p, senderSeen);
            return count;
        });
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ConsoleCloseRequested_NoSubscribers_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var p = new ConsolePanel();
            var method = typeof(ConsolePanel).GetMethod(
                "OnCloseClicked",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ex = Record.Exception(() => method!.Invoke(p, new object?[] { p, null! }));
            Assert.Null(ex);
            return true;
        });
    }
}