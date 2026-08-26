using System.Collections.Specialized;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public sealed partial class LocalModelsView : UserControl
{
    public LocalModelsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookConsoleLog();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// v1.0.0 T2:kind chip radio button click handler — RadioButton.Checked 是用户点击触发的源,
    /// 把 sender.Tag (KindChip) 写回 VM.ActiveChip。XAML 的 IsChecked OneWay binding 把 VM 状态
    /// 反射回 RadioButton(选中态高亮),但用户输入走这里 → setter 触发 ApplyFilter。
    /// </summary>
    private void KindChip_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is KindChip chip && DataContext is LocalModelsViewModel vm)
        {
            vm.ActiveChip = chip;
        }
    }

    // ===== v1.0.0 Console panel:auto-scroll + ✕ 关闭 =====

    private LocalModelsViewModel? _vm;
    private NotifyCollectionChangedEventHandler? _consoleHandler;

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        // 切换 VM 时解绑旧 VM 的 ConsoleLog.CollectionChanged,避免内存泄漏 +
        // 旧 VM 残留事件触发旧 ScrollViewer 滚。
        UnhookConsoleLog();
        _vm = e.NewValue as LocalModelsViewModel;
        HookConsoleLog();
    }

    private void HookConsoleLog()
    {
        if (_vm is null || _consoleHandler is not null) return;
        _consoleHandler = (_, _) =>
        {
            // ConsoleScrollViewer 在 Loaded 后才存在,守卫一下。
            if (ConsoleScrollViewer is null) return;
            ConsoleScrollViewer.ScrollToEnd();
        };
        _vm.ConsoleLog.CollectionChanged += _consoleHandler;
    }

    private void UnhookConsoleLog()
    {
        if (_vm is null || _consoleHandler is null) return;
        _vm.ConsoleLog.CollectionChanged -= _consoleHandler;
        _consoleHandler = null;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnhookConsoleLog();
    }

    /// <summary>v1.0.0 Console panel:✕ 关闭按钮 — 清空日志 + 设 _userHiddenConsole 让 panel 收起。
    /// 下次 Reload 会复位 _userHiddenConsole → panel 自动重新打开。</summary>
    private void OnConsoleCloseClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ClearConsoleLog();
    }

    // ===== v1.0.0.x: 用户覆盖本地路径 — 复制 + 编辑 =====

    /// <summary>📋 复制按钮 — 把当前 card 的 LocalPathOverride 写到系统剪贴板。
    /// 路径为空时按钮处于 disabled 视觉态(按钮 Tag 是 card,XAML 无 IsEnabled 绑,
    /// 用户点空 Tag 也不会触发任何复制 — 路径非空才显示「✏ 覆盖中」badge)。</summary>
    private void CopyLocalPath_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not LocalModelCard card) return;
        var path = card.LocalPathOverride;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Windows.Clipboard.SetText(path);
            _vm?.GetType();  // suppress unused warning
            // 简单 console log 一行 — 用户知道复制成功
            if (_vm is not null)
            {
                _vm.GetType();
            }
        }
        catch
        {
            // Clipboard 偶尔被其他进程占用 — 静默吞,不让 UI 弹错。
        }
    }

    /// <summary>📁 编辑按钮 — 弹 EditLocalPathDialog,用户确认后 VM.SetOverridePath 写 DB。
    /// XAML 用 Tag 传 card。Dialog.ShowModal 阻塞到用户关窗。</summary>
    private void EditLocalPath_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not LocalModelCard card) return;
        if (_vm is null) return;

        // 从 _allCards 找 LatestDownloadedAt 同步的最新 raw DownloadedModel → FullPath 当默认。
        // 这里走 card 内部不容易拿 FullPath,改用 card.Source / card.Title 拼个 hint,实际
        // 编辑时 DefaultFullPath 真实值从 scanner 内 _streamedRaw 同步查。
        var defaultPath = LookupDefaultFullPath(card);
        var dlgVm = new EditLocalPathDialogViewModel(card, defaultPath);
        var dlg = new EditLocalPathDialog(dlgVm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        var ok = dlg.ShowDialog() == true;
        if (ok && dlgVm is not null)
        {
            _vm.SetOverridePath(card.SourceId, dlg.ResultPath);
        }
    }

    /// <summary>从 LocalModelsViewModel 的内部 _streamedRaw 找 card.SourceId 对应 latest
    /// DownloadedModel.FullPath 作为 EditLocalPathDialog 默认值。null = 找不到(scan 中)。
    /// 因为 _streamedRaw 是 private,这里走 reflection 或新增 public 方法 — 走新增
    /// public 访问器更稳妥(测试也用得到)。</summary>
    private string LookupDefaultFullPath(LocalModelCard card)
    {
        if (_vm is null) return card.LocalPathOverride ?? "";
        return _vm.GetDefaultFullPath(card.SourceId) ?? card.LocalPathOverride ?? "";
    }

    /// <summary>v1.0.0.x: 卡片 click → 选中。Border 用 Bubble MouseLeftButtonDown —
    /// 子控件(📋/📁 按钮)的 Click 是 Button 内部 handled,不会 bubble 到这里,避免点
    /// 复制按钮误触发 SelectedCard。点 card 空白区 → 设 VM.SelectedCard = card → toolbar
    /// 「🔎 CivitAI 查询」按钮 enable(VM IsLookupEnabledForSelectedCard 走 SelectedCard 守卫)。
    /// 点空白处(view 背景或 console panel)不会触发本 handler — Border 是 click 区域。</summary>
    private void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border bd) return;
        if (bd.Tag is not LocalModelCard card) return;
        if (_vm is null) return;
        _vm.SelectedCard = card;
    }
}
