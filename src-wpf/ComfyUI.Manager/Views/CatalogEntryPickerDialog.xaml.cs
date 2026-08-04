using System;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class CatalogEntryPickerDialog : System.Windows.Window
{
    public CatalogEntry? Result { get; private set; }

    public CatalogEntryPickerDialog(CatalogEntryPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += () =>
        {
            Result = vm.Selected;
            DialogResult = Result is not null;
            Close();
        };
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is CatalogEntryPickerViewModel vm && vm.Selected is not null)
        {
            Result = vm.Selected;
            DialogResult = true;
            Close();
        }
    }

    /// <summary>
    /// Show():打开 catalog 条目 picker,返回选中的 entry(取消返回 null)。
    /// 内部用 CatalogCacheStore 默认路径(bin/data/catalog-cache.db),跟 App.xaml.cs 用的
    /// 是同一份 db。
    /// </summary>
    public new static CatalogEntry? Show()
    {
        var repo = new CatalogRepository(new CatalogCacheStore());
        var vm = new CatalogEntryPickerViewModel(repo);
        var dlg = new CatalogEntryPickerDialog(vm)
        {
            Owner = Application.Current.MainWindow,
        };
        dlg.ShowDialog();
        return dlg.Result;
    }
}