using System;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class CatalogEntryPickerDialog : System.Windows.Window
{
    public CatalogEntry? Result { get; private set; }

    public CatalogEntryPickerDialog(CatalogEntryPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseWithEntry += entry =>
        {
            Result = entry;
            DialogResult = true;
            Close();
        };
        vm.Cancelled += () =>
        {
            Result = null;
            DialogResult = false;
            Close();
        };
    }

    /// <summary>
    /// Show(envRepo, nodeOps, catalogRepo, nodeRepo, logger, envId):打开 picker,
    /// 绑定到指定 env 的安装状态。envId 非空时 picker 知道哪些 catalog 条目已
    /// 装入此 env,显示"已装"/"已过时" 徽标 + 行内卸载按钮。
    ///
    /// 取消返回 null;选中未装条目(Ok / 双击未装条目)返回 CatalogEntry,由 caller
    /// 接着弹 InstallDialog。repos 全部由 caller 注入(App.xaml.cs 在 T3 接线时
    /// 统一构造),保证 picker 跟其他 view 共享同一份 db 连接 / service 实例。
    ///
    /// 注意:catalogRepo / nodeRepo 是 CatalogPickerVM 用的(repo-level 数据访问);
    /// envRepo / nodeOps 是 NodeOperations 构造参数(env / git 操作)。
    /// </summary>
    public static CatalogEntry? Show(
        EnvironmentRepository envRepo,
        NodeOperations nodeOps,
        CatalogRepository catalogRepo,
        NodeRepository nodeRepo,
        AppLogger? logger,
        string envId)
    {
        var vm = new CatalogEntryPickerViewModel(
            catalogRepo, nodeRepo, nodeOps, envId, logger);
        var dlg = new CatalogEntryPickerDialog(vm)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        dlg.ShowDialog();
        return dlg.Result;
    }

    /// <summary>
    /// Deprecated 0-arg overload:保留作 T3 接线前的过渡 shim。
    /// 当前唯一调用方是 <c>EnvironmentListViewModel.OpenInstallNodePicker</c>(T3 替换为
    /// 6-arg 版本)。这里用默认 db 路径 + 空 envId 跑,跟以前行为等价(无 env-aware
    /// "已装"标记,但条目仍可正常选中走 InstallDialog)。
    /// </summary>
    public new static CatalogEntry? Show()
    {
        var dbFactory = new SqliteConnectionFactory();
        var envRepo = new EnvironmentRepository(dbFactory);
        var nodeRepo = new NodeRepository(dbFactory);
        var catalogCache = new CatalogCacheStore();
        var catalogRepo = new CatalogRepository(catalogCache);
        var diffService = new NodeInstallDiffService(
            (_, _, _, _) => System.Threading.Tasks.Task.FromResult(
                new ComfyUI.Manager.Infrastructure.ProcessResult(true, 0, "[]", "")));
        var nodeOps = new NodeOperations(
            new GitRunner("git"), envRepo, nodeRepo, new Settings(), diffService);
        return Show(envRepo, nodeOps, catalogRepo, nodeRepo, null, envId: "");
    }
}