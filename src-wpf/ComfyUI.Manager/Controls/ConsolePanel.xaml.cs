using System;
using System.Collections;
using System.Collections.Specialized;
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
        LinesItemsControl.ItemsSource = Lines;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        ConsoleCloseRequested?.Invoke(this, EventArgs.Empty);
    }
}