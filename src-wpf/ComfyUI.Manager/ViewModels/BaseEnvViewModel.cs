using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// 基础环境部署 page 的 VM:
/// - 顶部 profile 多选 + 底部 env 多选 + Start 按钮(单页面,无 dialog)
/// - profiles 来源:从 <see cref="BaseEnvProfileLoader"/> 加载(无 JSON → 5 个内置默认)
/// - envs 来源:从 <see cref="EnvironmentRepository"/> 读 SQLite
/// - 点击 Start → 弹 <see cref="BaseEnvProgressDialog"/>(取第一个选中的 profile)
/// </summary>
public class BaseEnvViewModel : ViewModelBase
{
    private readonly BaseEnvProfileLoader _loader;
    private readonly EnvironmentRepository _envRepo;
    private readonly BaseEnvInstaller _installer;

    private readonly List<BaseEnvProfile> _selectedProfiles = new();
    private readonly List<string> _selectedEnvIds = new();

    public BaseEnvViewModel(
        BaseEnvProfileLoader loader,
        EnvironmentRepository envRepo,
        BaseEnvInstaller installer)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));

        Profiles = new ObservableCollection<BaseEnvProfile>();
        Envs = new ObservableCollection<EnvModel>();

        StartCommand = new RelayCommand(
            _ => Start(),
            _ => CanStart());
    }

    /// <summary>绑定到 profile ListBox.ItemsSource。</summary>
    public ObservableCollection<BaseEnvProfile> Profiles { get; }

    /// <summary>绑定到 env ListBox.ItemsSource。</summary>
    public ObservableCollection<EnvModel> Envs { get; }

    /// <summary>
    /// 只读视图:当前选中的 profiles(XAML 不直接 bind,供测试 + dialog-launch)。
    /// </summary>
    public IReadOnlyList<BaseEnvProfile> SelectedProfiles => _selectedProfiles;

    /// <summary>
    /// 只读视图:当前选中的 env ids(XAML 不直接 bind,供测试 + dialog-launch)。
    /// </summary>
    public IReadOnlyList<string> SelectedEnvIds => _selectedEnvIds;

    /// <summary>
    /// 启动命令(canExecute:同时需要 ≥1 profile 和 ≥1 env)。
    /// </summary>
    public RelayCommand StartCommand { get; }

    /// <summary>
    /// 测试 seam:生产代码走 <see cref="BaseEnvProgressDialog.Show"/> 静态入口。
    /// 单测可赋值来拦截 ShowDialog 调用、断言参数。
    /// </summary>
    public Action<IReadOnlyList<string>, BaseEnvProfile, BaseEnvInstaller>? ShowDialogOverride { get; set; }

    /// <summary>
    /// 加载 profiles(envs)并填充 ObservableCollection。
    /// - 第二次调用视为刷新:envs 会重读,profiles 也会重新加载。
    /// - 空 repo / 空 JSON 都是合法状态(UI 负责展示 empty-state,VM 不抛)。
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var profiles = await _loader.LoadAsync(ct).ConfigureAwait(true);
        var envs = _envRepo.ListAll();

        Profiles.Clear();
        foreach (var p in profiles)
        {
            Profiles.Add(p);
        }

        Envs.Clear();
        foreach (var e in envs)
        {
            Envs.Add(e);
        }

        // 重新加载后,之前的 selection 可能已无效 → 清空并刷新 CanExecute。
        _selectedProfiles.Clear();
        _selectedEnvIds.Clear();
        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 由 XAML SelectionChanged 调用:整体替换当前选中的 profiles 列表。
    /// (XAML 端 ListBox.SelectionMode=Extended,把 SelectedItems 直接传过来即可。)
    /// </summary>
    public void SetSelectedProfiles(IEnumerable<BaseEnvProfile> selection)
    {
        if (selection is null) throw new ArgumentNullException(nameof(selection));
        _selectedProfiles.Clear();
        _selectedProfiles.AddRange(selection);
        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 由 XAML SelectionChanged 调用:整体替换当前选中的 env id 列表(从 Environment.Id 提取)。
    /// </summary>
    public void SetSelectedEnvIds(IEnumerable<EnvModel> selection)
    {
        if (selection is null) throw new ArgumentNullException(nameof(selection));
        _selectedEnvIds.Clear();
        _selectedEnvIds.AddRange(selection.Select(e => e.Id));
        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Start 按钮执行:取第一个选中的 profile,弹 BaseEnvProgressDialog。
    /// 多 profile 选择简化(G5):只跑第一个;后续 hotfix 可拓展为逐个。
    /// </summary>
    public void Start()
    {
        if (_selectedProfiles.Count == 0 || _selectedEnvIds.Count == 0) return;

        var profile = _selectedProfiles[0];
        var envIds = _selectedEnvIds.ToList();

        if (ShowDialogOverride is not null)
        {
            ShowDialogOverride(envIds, profile, _installer);
        }
        else
        {
            BaseEnvProgressDialog.Show(envIds, profile, _installer);
        }
    }

    private bool CanStart()
        => _selectedProfiles.Count > 0 && _selectedEnvIds.Count > 0;
}
