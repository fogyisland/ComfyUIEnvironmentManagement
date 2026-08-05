using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.ViewModels;

/// <summary>
/// 基础环境部署 page 的 VM:
/// - 顶部 ComboBox 选 PyTorch 版本 + profile 多选 + 底部 env 多选 + Start 按钮
/// - profile 数据源由 <see cref="IsUserOverrideActive"/> 二选一:
///   * 用户 override(<c>&lt;appDataDir&gt;/base_env_profiles.json</c> 存在且合法)
///     → 直接用文件内容,<see cref="Versions"/> 为空,UI 隐藏 ComboBox。
///   * 多版本模式(无 override)
///     → <see cref="PyTorchVersionDirectory"/> 拉 stable + nightly 列表,
///     默认选第一个 stable;切换版本时 async reload 对应 profile。
/// - envs 来源:从 <see cref="EnvironmentRepository"/> 读 SQLite
/// - 点击 Start → 弹 <see cref="BaseEnvProgressDialog"/>(取第一个选中的 profile)
/// </summary>
public class BaseEnvViewModel : ViewModelBase
{
    private readonly BaseEnvProfileLoader _loader;
    private readonly EnvironmentRepository _envRepo;
    private readonly BaseEnvInstaller _installer;
    private readonly PyTorchVersionDirectory _directory;
    private readonly string _appDataDir;

    private readonly List<BaseEnvProfile> _selectedProfiles = new();
    private readonly List<string> _selectedEnvIds = new();

    /// <summary>
    /// 防止 stale async load 覆盖较新的 SelectedVersion 切换结果。
    /// 每次 SelectedVersion 切换自增;older loads 完成后会被丢弃。
    /// </summary>
    private int _loadGeneration;

    /// <summary>
    /// 最近一次 profile reload 的 <see cref="Task"/>(包括 <see cref="LoadAsync"/>
    /// 内的初始 reload 和 setter 触发的 reload)。测试可以 await 它来等待
    /// async work 结束再断言 <see cref="Profiles"/>。
    /// </summary>
    public Task LastReloadTask => _lastReloadTask ?? Task.CompletedTask;
    private Task? _lastReloadTask;

    public BaseEnvViewModel(
        BaseEnvProfileLoader loader,
        EnvironmentRepository envRepo,
        BaseEnvInstaller installer,
        PyTorchVersionDirectory directory,
        string appDataDir)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _envRepo = envRepo ?? throw new ArgumentNullException(nameof(envRepo));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        if (string.IsNullOrWhiteSpace(appDataDir))
        {
            throw new ArgumentException("appDataDir must be non-empty", nameof(appDataDir));
        }
        _appDataDir = appDataDir;

        Profiles = new ObservableCollection<BaseEnvProfile>();
        Envs = new ObservableCollection<EnvModel>();
        Versions = new ObservableCollection<PyTorchVersionEntry>();

        StartCommand = new RelayCommand(
            _ => Start(),
            _ => CanStart());
    }

    /// <summary>绑定到 profile ListBox.ItemsSource。</summary>
    public ObservableCollection<BaseEnvProfile> Profiles { get; }

    /// <summary>绑定到 env ListBox.ItemsSource。</summary>
    public ObservableCollection<EnvModel> Envs { get; }

    /// <summary>
    /// 绑定到 PyTorch 版本 ComboBox.ItemsSource。空 = 用户 override 模式
    /// (T6 XAML 用 <c>Versions.Count == 0</c> 或 <see cref="IsUserOverrideActive"/>
    /// 隐藏 ComboBox)。
    /// </summary>
    public ObservableCollection<PyTorchVersionEntry> Versions { get; }

    /// <summary>
    /// ComboBox SelectedItem 绑定。Set 触发 async profile reload(用
    /// <see cref="_loadGeneration"/> 防止 stale overwrite)。
    /// </summary>
    public PyTorchVersionEntry? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                _lastReloadTask = OnSelectedVersionChangedAsync(value);
            }
        }
    }
    private PyTorchVersionEntry? _selectedVersion;

    /// <summary>
    /// <c>true</c> = 用户 override 模式(<c>base_env_profiles.json</c> 文件
    /// 存在且合法,直接用文件内容,UI 隐藏 ComboBox);
    /// <c>false</c> = 多版本模式(ComboBox 可见,版本切换会 reload profiles)。
    /// </summary>
    public bool IsUserOverrideActive
    {
        get => _isUserOverrideActive;
        private set => SetField(ref _isUserOverrideActive, value);
    }
    private bool _isUserOverrideActive;

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
    /// 测试 seam:生产代码弹 MessageBox("已安装" 提示),单测可赋值 trap 避免挂死。
    /// </summary>
    public Action<string>? MessageBoxOverride { get; set; }

    /// <summary>
    /// 加载 profiles(envs)并填充 ObservableCollection。
    /// 二次调用视为刷新。
    /// <para>
    /// 用户 override 优先级:若 <c>&lt;appDataDir&gt;/base_env_profiles.json</c>
    /// 存在且合法,直接用文件内容并设 <see cref="IsUserOverrideActive"/> = true,
    /// <see cref="Versions"/> 留空,UI 不显示 ComboBox。
    /// </para>
    /// <para>
    /// 否则走多版本模式:从 <see cref="PyTorchVersionDirectory"/> 拉目录,
    /// 默认 <see cref="SelectedVersion"/> = 第一个 stable 项,async 加载
    /// 对应 profile。返回的 <see cref="Task"/> 会在初始 profile reload 完成后
    /// complete(此时 <see cref="Profiles"/> 已被填充,测试可直接断言)。
    /// </para>
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        // 重置 generation,丢掉任何在飞的 stale load。
        Interlocked.Increment(ref _loadGeneration);

        var envs = _envRepo.ListAll();

        if (TryLoadUserOverride(out var userOverrideProfiles))
        {
            // 用户 override 路径:忽略 directory,直接用文件内容。
            IsUserOverrideActive = true;
            // Suppress the setter's fire-and-forget reload by nulling without
            // triggering the side-effect: clear Versions first so the setter
            // sees no entry and yields empty profiles (which we then overwrite
            // with the override list below).
            _selectedVersion = null;
            RaisePropertyChanged(nameof(SelectedVersion));
            Versions.Clear();
            ReplaceProfiles(userOverrideProfiles);
            ReplaceEnvs(envs);
            _selectedProfiles.Clear();
            _selectedEnvIds.Clear();
            StartCommand.RaiseCanExecuteChanged();
            return;
        }

        // 多版本模式。
        IsUserOverrideActive = false;
        var entries = await _directory.GetAllAsync(ct).ConfigureAwait(true);

        // nightly 永远在 [0],stable 从 [1] 开始;默认选第一个 stable。
        Versions.Clear();
        foreach (var e in entries) Versions.Add(e);
        var firstStable = Versions.FirstOrDefault(e => !e.IsNightly);
        ReplaceEnvs(envs);
        _selectedProfiles.Clear();
        _selectedEnvIds.Clear();
        StartCommand.RaiseCanExecuteChanged();

        // 设置 SelectedVersion 之前,先把 generation + 1,让 setter 里的
        // OnSelectedVersionChangedAsync 不会跟我们的后续手动 await 竞争。
        // Setter 触发时 generation 自增到 N+1;我们在下面直接 await 一次 reload,
        // generation 又自增到 N+2,setter 的 check 失效(被 N+2 取代)→ 被丢弃。
        // 这样 await LoadAsync() 完成后 Profiles 一定已包含 firstStable 的 profile。
        Interlocked.Increment(ref _loadGeneration);
        _selectedVersion = firstStable;
        RaisePropertyChanged(nameof(SelectedVersion));
        if (firstStable is not null)
        {
            var task = ReloadProfilesForVersionAsync(firstStable);
            _lastReloadTask = task;
            await task.ConfigureAwait(true);
        }
    }

    /// <summary>
    /// <see cref="SelectedVersion"/> setter 触发的 async reload (fire-and-forget)。
    /// 用 <see cref="_loadGeneration"/> 防 stale:本次调用结束前若 generation
    /// 被自增(用户切到别的版本或 <see cref="LoadAsync"/> 重新初始化),丢弃结果。
    /// </summary>
    private async Task OnSelectedVersionChangedAsync(PyTorchVersionEntry? entry)
    {
        await ReloadProfilesForVersionAsync(entry).ConfigureAwait(true);
    }

    /// <summary>
    /// 实际拉取并填充 <see cref="Profiles"/> 的核心方法。被 <see cref="LoadAsync"/>
    /// 直接 await,被 setter 异步调用。stale-load 通过 <see cref="_loadGeneration"/>
    /// 计数器丢弃。
    /// </summary>
    private async Task ReloadProfilesForVersionAsync(PyTorchVersionEntry? entry)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);

        IReadOnlyList<BaseEnvProfile> profiles;
        if (entry is null)
        {
            profiles = Array.Empty<BaseEnvProfile>();
        }
        else if (entry.IsNightly)
        {
            // nightly 永远走固定 cu126 单 profile loader 入口,不需要 metadata。
            profiles = await _loader
                .LoadProfilesForVersionAsync(PyTorchVersionDirectory.NightlyVersion, metadata: null)
                .ConfigureAwait(true);
        }
        else
        {
            // stable:把 entry 的 StableMetadata 喂给 loader,精确按 CudaVariants + HasCpu 生成。
            profiles = await _loader
                .LoadProfilesForVersionAsync(entry.Version, entry.StableMetadata)
                .ConfigureAwait(true);
        }

        // 这次 load 已经被新 selection 取代 → 丢弃,别覆盖 Profiles。
        if (generation != Volatile.Read(ref _loadGeneration)) return;

        ReplaceProfiles(profiles);
        _selectedProfiles.Clear();
        StartCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 用文件内容尝试 user override 加载。返回 true 表示文件存在且 JSON 合法;
    /// false 表示文件不存在 / 损坏 / 空,VM 应走多版本路径。
    /// </summary>
    private bool TryLoadUserOverride(out IReadOnlyList<BaseEnvProfile> profiles)
    {
        profiles = Array.Empty<BaseEnvProfile>();
        var path = Path.Combine(_appDataDir, BaseEnvProfileLoader.FileName);
        if (!File.Exists(path)) return false;
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<BaseEnvProfile>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null) return false;
            profiles = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ReplaceProfiles(IEnumerable<BaseEnvProfile> profiles)
    {
        Profiles.Clear();
        foreach (var p in profiles) Profiles.Add(p);
    }

    private void ReplaceEnvs(IEnumerable<EnvModel> envs)
    {
        Envs.Clear();
        foreach (var e in envs) Envs.Add(e);
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
    /// <para>
    /// 全部选中 env 已 <c>BedStatus == "done"</c> 时弹"已安装"消息,跳过 install dialog
    /// (v0.6.5.19 hotfix — 用户报"完成后再点 BED 再次安装" → 期望弹已安装提示)。
    /// </para>
    /// </summary>
    public void Start()
    {
        if (_selectedProfiles.Count == 0 || _selectedEnvIds.Count == 0) return;

        // 全部 selected envIds 都能查到 且 BedStatus == "done" → 跳过 install,
        // 弹"已安装"提示。利用 _envRepo 实时读 — checkbox 跟 sqlite 状态可能
        // 跟界面 Load 不同步(用户上次装后没重新进 BED 页面)。
        var existingEnvs = _selectedEnvIds
            .Select(id => _envRepo.Get(id))
            .Where(e => e is not null)
            .ToList();
        if (existingEnvs.Count == _selectedEnvIds.Count
            && existingEnvs.All(e => e!.BedStatus == "done"))
        {
            var names = string.Join(", ", existingEnvs.Select(e => e!.Name));
            ShowAlreadyInstalled(
                $"所选 env 已安装基础环境,无需再装:{names}");
            return;
        }

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

    private void ShowAlreadyInstalled(string message)
    {
        if (MessageBoxOverride is not null)
        {
            MessageBoxOverride(message);
            return;
        }
        MessageBox.Show(
            message, "已安装",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool CanStart()
        => _selectedProfiles.Count > 0 && _selectedEnvIds.Count > 0;
}
