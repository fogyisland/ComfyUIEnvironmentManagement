using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ComfyUI.Manager.Controls;

/// <summary>
/// v1.0.0.x #590:统一 5 处 console 面板的 UserControl。
/// <para>
/// 行为:
/// - 绑定 <see cref="Lines"/> (任何 <see cref="IEnumerable"/>,通常 <c>ObservableCollection&lt;string&gt;</c>)
/// - 用户点 ✕ → raise <see cref="ConsoleCloseRequested"/>,parent view code-behind 处理
/// - <see cref="Lines"/> 是 <see cref="INotifyCollectionChanged"/> 时,新行追加自动 ScrollToEnd
///   (parent 不再需要手动 hook ConsoleLog.CollectionChanged)
/// - 复制:ListBox SelectionMode=Extended → WPF 内置 Ctrl+C 复制 SelectedItems,Ctrl+A 全选;
///   toolbar "复制" 按钮兜底(无焦点场景:有选中复制选中,无选中复制全部)
/// </para>
/// </summary>
public partial class ConsolePanel : UserControl
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IEnumerable), typeof(ConsolePanel),
        new PropertyMetadata(null, OnLinesChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ConsolePanel),
        new PropertyMetadata("Console"));

    public IEnumerable? Lines
    {
        get => (IEnumerable?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>用户点 ✕ 按钮时 raise — parent view code-behind 处理(典型动作:调 VM.ClearConsoleLog)。</summary>
    public event EventHandler? ConsoleCloseRequested;

    private INotifyCollectionChanged? _hookedSource;

    public ConsolePanel()
    {
        InitializeComponent();
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ConsolePanel)d;
        self.RehookCollectionChanged(e.NewValue as INotifyCollectionChanged);
        self.RefreshItems();
    }

    private void RehookCollectionChanged(INotifyCollectionChanged? newSource)
    {
        if (ReferenceEquals(_hookedSource, newSource)) return;

        if (_hookedSource is not null)
        {
            _hookedSource.CollectionChanged -= OnSourceCollectionChanged;
            _hookedSource = null;
        }

        if (newSource is not null)
        {
            newSource.CollectionChanged += OnSourceCollectionChanged;
            _hookedSource = newSource;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新行追加时滚到底。ScrollViewer 在 Loaded 后才存在,守卫一下。
        if (ConsoleScrollViewer is null) return;
        ConsoleScrollViewer.ScrollToEnd();
    }

    private void RefreshItems()
    {
        LinesListBox.ItemsSource = Lines;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        ConsoleCloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// v1.0.0.x 用户原话"console 的日志可以复制到剪贴板,目前是不行的":
    /// 有选中行 → 复制 SelectedItems joined by Environment.NewLine;
    /// 无选中行 → 复制全部 Lines(同一规则);Lines 为 null / 空 → no-op。
    /// 兜底 ListBox 无 keyboard focus 的场景(用户没先点 ListBox 就点按钮)。
    /// 复制失败(剪贴板被其他进程占用 COM exception)静默吞 — 不弹错误打扰。
    /// </summary>
    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        string text;
        if (LinesListBox.SelectedItems.Count > 0)
        {
            text = string.Join(Environment.NewLine, LinesListBox.SelectedItems.Cast<string>());
        }
        else if (Lines is not null)
        {
            // IEnumerable<string>;Lines.Cast<string>() 在 Lines 是 string[] 时不抛,
            // 在是 List<string> 时也走 ToString 直接 ToArray,统一。
            var all = Lines.Cast<string>().ToArray();
            if (all.Length == 0) return;
            text = string.Join(Environment.NewLine, all);
        }
        else
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被其他进程独占(典型:远程桌面 / clip.exe / OneDrive 同步)
            // → COMException。静默吞,UI 不弹错误打扰。
        }
    }
}