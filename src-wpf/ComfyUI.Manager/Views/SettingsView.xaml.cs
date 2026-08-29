using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SyncTokenFromViewModel();
            SyncSectionScrollSubscription();
        };
    }

    private SettingsViewModel? _vm;
    private bool _scrollSubscribed;
    // v0.6.14.1 hotfix:SyncTokenFromViewModel 设 GitHubTokenBox.Password 时
    // PasswordBox 内部会触发 PasswordChanged → OnGitHubTokenChanged → VM 标
    // Dirty["GitHubToken"] → ⚠"尚未保存" 警告一直亮。_syncingToken 标志让
    // OnGitHubTokenChanged 在 sync 期间短路,避免回环。
    private bool _syncingToken;

    /// <summary>
    /// v0.6.9 T7:订阅 VM 的 SectionScrollRequested event。DataContext 变化时重新订阅
    /// (MainViewModel 缓存同一份 VM,所以正常情况只会订阅一次,但重置保险)。
    /// </summary>
    private void SyncSectionScrollSubscription()
    {
        if (_vm is not null && _scrollSubscribed)
        {
            _vm.SectionScrollRequested -= OnSectionScrollRequested;
            _scrollSubscribed = false;
        }
        if (DataContext is SettingsViewModel vm)
        {
            _vm = vm;
            vm.SectionScrollRequested += OnSectionScrollRequested;
            _scrollSubscribed = true;
        }
    }

    private void OnSectionScrollRequested(object? sender, string sectionKey)
    {
        // 找 x:Name="Section{sectionKey}" 的 TextBlock,HitTest / ScrollIntoView。
        // FindName 在 UserControl 上找 — 必须先用 UpdateLayout 让 x:Name 注册进来。
        if (string.IsNullOrEmpty(sectionKey)) return;
        UpdateLayout();
        var element = FindName($"Section{sectionKey}") as FrameworkElement;
        element?.BringIntoView();
    }

    private void SyncTokenFromViewModel()
    {
        // PasswordBox 不参与 XAML 双向绑定(string 会明文显示),
        // 首次加载时把 VM 里已存的 token 灌进 PasswordBox。
        // _syncingToken 防止 PasswordBox.Password = X 触发的回环标 Dirty。
        if (DataContext is SettingsViewModel vm && GitHubTokenBox.Password != vm.GitHubToken)
        {
            _syncingToken = true;
            try
            {
                GitHubTokenBox.Password = vm.GitHubToken;
            }
            finally
            {
                _syncingToken = false;
            }
        }
    }

    private void OnGitHubTokenChanged(object sender, RoutedEventArgs e)
    {
        // sync 期间短路:PasswordBox.Password = vm.GitHubToken 内部触发的
        // PasswordChanged 不应被视为用户编辑。
        if (_syncingToken) return;
        if (DataContext is SettingsViewModel vm && sender is PasswordBox pb)
        {
            vm.GitHubToken = pb.Password;
        }
    }

    private void BrowseEnvsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.EnvsDir = picked;
        }
    }

    /// <summary>
    /// v1.0.0.x #630: 把系统模板库目录浏览按钮加回 Settings 页 ——
    /// 用户原话"为什么设置中的模板目录不见了"。PickFolder 用 WPF OpenFolderDialog,
    /// 选完后把绝对路径写到 VM.SystemTemplateLibraryDir(MarkDirty 自动触发 ⚠ 警告)。
    /// </summary>
    private void BrowseSystemTemplateLibraryDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.SystemTemplateLibraryDir = picked;
        }
    }

    private void BrowseGlobalNodesDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.GlobalNodesDir = picked;
        }
    }

    private void BrowseLocalNodeDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.LocalNodeDirectory = picked;
        }
    }

    // v1.0.0.x #577:本地常用节点根目录的浏览按钮(handler 配 BrowseLocalNodesDir XAML)。
    private void BrowseLocalNodesDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.LocalNodesDirectory = picked;
        }
    }

    // v1.0.0.x: 系统模板库目录(Settings.SystemTemplateLibraryDir)改内置,Settings 页
// 不再暴露 UI,这里 BrowseSystemTemplateLibraryDir 也删除(handler 不再被 XAML 调用)。

    private void BrowseWorkflowsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.WorkflowsDirectory = picked;
        }
    }

    private void OpenWorkflowsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var raw = vm.WorkflowsDirectory;
        if (string.IsNullOrWhiteSpace(raw)) return;
        // WorkflowsDirectory 可为相对子目录名(如 "workflows"),以 AppContext.BaseDirectory 解绝对
        var path = Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch
        {
            // 失败静默 — 用户用 "浏览..." 按钮 + 自己打开 explorer 也行
        }
    }

    // v0.6.22+:ModelsDirectory 字段已硬删,模型市场下载目录 = DefaultModelsDirectory。
// 原 BrowseModelsDir / OpenModelsDir 两个 handler 已删 — 用户改 DefaultModelsDirectory 用现有
// BrowseDefaultModelsDirectory handler。

    private void BrowseDefaultModelsDirectory(object sender, RoutedEventArgs e)
    {
        var picked = DataContext is SettingsViewModel vm ? vm.PickFolder() : null;
        if (!string.IsNullOrEmpty(picked) && DataContext is SettingsViewModel vm2)
        {
            vm2.DefaultModelsDirectory = picked;
        }
    }

    // ============ v1.0.0.x (2026-08-29):Forge 模型目录 6 个 per-type 浏览按钮 ============
    // 镜像 BrowseDefaultModelsDirectory 模式:vm.PickFolder() 返 nullable string,
    // 非空时写回对应 property(setter 内部 MarkDirty 自动触发 ⚠ 警告)。
    // x:Name="ForgePathsGroupBox" 仍保留 — JumpToForgePaths.FindName 依赖。

    private void BrowseForgeCheckpointsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.CheckpointsDir = picked;
        }
    }

    private void BrowseForgeLorasDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.LorasDir = picked;
        }
    }

    private void BrowseForgeVaeDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.VaeDir = picked;
        }
    }

    private void BrowseForgeEmbeddingsDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.EmbeddingsDir = picked;
        }
    }

    private void BrowseForgeHypernetworksDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.HypernetworksDir = picked;
        }
    }

    private void BrowseForgeControlnetDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.ControlnetDir = picked;
        }
    }

    private void BrowsePythonVenvBaseline(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("Python 解释器", "python.exe|python.exe;python3.exe|所有文件|*.*");
            if (picked is not null) vm.PythonVenvBaseline = picked;
        }
    }

    private void BrowseGitExe(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("git.exe", "git.exe|git.exe|所有文件|*.*");
            if (picked is not null) vm.GitExe = picked;
        }
    }

    private void BrowsePythonInterpreter(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFile("选择 Python 解释器", "可执行文件|*.exe|所有文件|*.*");
            if (picked is not null) vm.NewPythonInterpreterPath = picked;
        }
    }

    private void BrowseExtraPath(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ExtraPath ep } && DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) ep.Path = picked;
        }
    }

    // ============ v0.6.21: 模型市场扩展 handler ============

    /// <summary>
    /// Test HuggingFace connection: probes /api/whoami-v2 endpoint on the chosen base URL
    /// (mirror or official). Token sent as Bearer header only over HTTPS.
    /// All errors surfaced via MessageBox (per spec §7 security policy).
    /// </summary>
    private void TestHuggingFaceConnection(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var baseUrl = vm.ModelSourceHuggingFaceUseMirror && !string.IsNullOrWhiteSpace(vm.ModelSourceHuggingFaceMirrorUrl)
            ? vm.ModelSourceHuggingFaceMirrorUrl.TrimEnd('/')
            : "https://huggingface.co";
        var token = vm.HuggingFaceApiToken;

        // HTTP mirror with token → refuse (security policy from spec §7)
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(token))
        {
            MessageBox.Show($"镜像 {baseUrl} 使用 http,不发送 token。\n请改用 https 镜像或临时清空 token。",
                "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Fire-and-forget async probe
        _ = ProbeHuggingFaceConnectionAsync(baseUrl, token);
    }

    private async Task ProbeHuggingFaceConnectionAsync(string baseUrl, string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(token))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var resp = await client.GetAsync($"{baseUrl}/api/whoami-v2").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"✅ 连接成功 ({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            else
            {
                Dispatcher.Invoke(() => MessageBox.Show($"❌ 失败 {(int)resp.StatusCode} {resp.ReasonPhrase}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"❌ 连接失败: {ex.Message}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void ResetHuggingFaceMirrorUrl(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ModelSourceHuggingFaceMirrorUrl = "https://hf-mirror.com";
        }
    }

    /// <summary>
    /// v0.6.22.x:Test ModelScope connection — mirror HF/CivitAI 测试连接 UX。
    /// ModelScope 没有 whoami endpoint,改用 /api/v1/models?limit=1 探测(轻量 + 返 models list)。
    /// 镜像切换优先于官方(token 决定是否注入 Authorization: Bearer header)。
    /// </summary>
    private void TestModelScopeConnection(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var baseUrl = vm.ModelSourceModelScopeUseMirror && !string.IsNullOrWhiteSpace(vm.ModelSourceModelScopeMirrorUrl)
            ? vm.ModelSourceModelScopeMirrorUrl.TrimEnd('/')
            : "https://www.modelscope.cn";
        var token = vm.ModelSourceModelScopeApiToken;

        // HTTP mirror with token → refuse (防 token 明文泄露)
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(token))
        {
            MessageBox.Show($"镜像 {baseUrl} 使用 http,不发送 token。\n请改用 https 镜像或临时清空 token。",
                "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = ProbeModelScopeConnectionAsync(baseUrl, token);
    }

    private async Task ProbeModelScopeConnectionAsync(string baseUrl, string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(token) && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // ModelScope 无 whoami endpoint,用 /api/v1/models?limit=1 探测 — 返 200 即连接 OK,
            // 401/403 即鉴权失败(token 错/失效/缺)。
            var resp = await client.GetAsync($"{baseUrl}/api/v1/models?limit=1").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"✅ 连接成功 ({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            else
            {
                Dispatcher.Invoke(() => MessageBox.Show($"❌ 失败 {(int)resp.StatusCode} {resp.ReasonPhrase}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"❌ 连接失败: {ex.Message}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void ResetModelScopeMirrorUrl(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.ResetModelScopeMirrorUrlCommand.Execute(null);
        }
    }

    /// <summary>
    /// v0.6.22+:CivitAI 测试连接 — 镜像 HuggingFace 测试连接 UX。
    /// 用当前 baseUrl(token 决定是否注入 Authorization: Bearer)probe CivitAI API。
    /// 注:CivitAI 没有 whoami-v2 endpoint,改用 /api/v1/models?limit=1 探测(轻量 + 返 models list)。
    /// </summary>
    private void TestCivitAiConnection(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var baseUrl = vm.ModelSourceCivitAiUseMirror && !string.IsNullOrWhiteSpace(vm.ModelSourceCivitAiMirrorUrl)
            ? vm.ModelSourceCivitAiMirrorUrl.TrimEnd('/')
            : "https://civitai.com";
        var token = vm.CivitAiApiToken;

        // HTTP mirror with token → refuse (同 HF 政策 — 防 token 明文泄露)
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(token))
        {
            MessageBox.Show($"镜像 {baseUrl} 使用 http,不发送 token。\n请改用 https 镜像或临时清空 token。",
                "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _ = ProbeCivitAiConnectionAsync(baseUrl, token);
    }

    private async Task ProbeCivitAiConnectionAsync(string baseUrl, string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrEmpty(token) && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            // CivitAI 无 whoami endpoint,用 /api/v1/models?limit=1 探测 — 返 200 即连接 OK,
            // 401/403 即鉴权失败(token 错/失效/缺)。
            var resp = await client.GetAsync($"{baseUrl}/api/v1/models?limit=1").ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"✅ 连接成功 ({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Information));
            }
            else
            {
                Dispatcher.Invoke(() => MessageBox.Show($"❌ 失败 {(int)resp.StatusCode} {resp.ReasonPhrase}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => MessageBox.Show($"❌ 连接失败: {ex.Message}\n({baseUrl})", "测试连接", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void RefreshModelMarketplace(object sender, RoutedEventArgs e)
    {
        // Find MainViewModel and call its refresh entry point
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel mvm)
        {
            mvm.RefreshModelMarketplace();
        }
    }

    private void OpenHyperlink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>
    /// v1.0.0.x #590:ConsolePanel 内置 ✕ 按钮 raise 此事件 — 桥接到 VM 的 CloseCommonNodeDownloadStatusCommand。
    /// ConsoleCloseRequested 是 EventHandler(非 RoutedEvent),参数类型用 EventArgs。
    /// </summary>
    private void OnCommonNodeDownloadConsoleCloseClicked(object sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.CloseCommonNodeDownloadStatusCommand.Execute(null);
        }
    }

    /// <summary>
    /// v1.0.0.x (2026-08-29):env-create 后 Forge 提示框「去设置」入口 —
    /// 把 Forge 模型目录 <c>GroupBox</c> 滚到视口里 + 短暂高亮(2s BorderBrush 变橙),
    /// 引导用户修改 LoRA/VAE 等 6 个 per-type 路径。
    ///
    /// <para>
    /// 调用方应在 SettingsView.Loaded 事件里调本方法(不是构造完立即调),否则
    /// <c>UpdateLayout()</c> 之前 x:Name 元素还没注册,FindName 返 null。
    /// canonical 调用模式(由 MainViewModel.OpenSettingsAndJumpToForgePaths 用):
    /// </para>
    /// <code>
    /// settingsView.Loaded += (s, e) => settingsView.JumpToForgePaths(afterShown: () => {
    ///     // 选填:用户改完路径后,下次 Forge env 启动时 ProcessLauncher.BuildStartCommand
    ///     // 自动从 settings.ForgePaths 派生 --*dir CLI arg,无需手动重写 yaml。
    /// });
    /// </code>
    ///
    /// <para>
    /// 如果 XAML 还没加 <c>x:Name="ForgePathsGroupBox"</c>(未来重构遗漏),FindName 返 null,
    /// 本方法 silent no-op + Debug.WriteLine 警告 — 不抛,不影响用户进 Settings 页。
    /// </para>
    /// </summary>
    /// <param name="afterShown">选填回调,在 scroll + highlight 完成后 invoke。
    /// caller 通常在此回调里重写刚创建 env 的 extra_model_paths.yaml。</param>
    public void JumpToForgePaths(Action? afterShown = null)
    {
        // FindName 需要 x:Name 元素已注册;Loaded 后 UpdateLayout 已完成。
        UpdateLayout();
        // Cast to Control 而不是 FrameworkElement — BorderBrush/BorderThickness 是 Control 的属性
        // (GroupBox 通过 Control 继承,BringIntoView 通过 FrameworkElement 继承)。
        var element = FindName("ForgePathsGroupBox") as Control;
        if (element is null)
        {
            // x:Name 缺失(理论上不会发生 — SettingsView.xaml:139 已加,这里 defense-in-depth)。
            Debug.WriteLine(
                "[SettingsView.JumpToForgePaths] ForgePathsGroupBox x:Name 未找到,skip scroll/highlight");
            afterShown?.Invoke();
            return;
        }

        // 1) ScrollViewer 滚到 GroupBox(BringIntoView 走 ScrollIntoView 语义)。
        element.BringIntoView();

        // 2) 临时把 BorderBrush 设成橙色,2s 后还原 — 高亮提示用户。
        // 用 DispatcherTimer 比 async Task.Delay 更 WPF 友好(走 UI thread 同步队列)。
        var originalBrush = element.BorderBrush;
        var originalThickness = element.BorderThickness;
        var highlightBrush = System.Windows.Media.Brushes.Orange;
        element.BorderBrush = highlightBrush;
        element.BorderThickness = new Thickness(2);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            element.BorderBrush = originalBrush;
            element.BorderThickness = originalThickness;
            // 回调在还原之后调 — caller(如 MainViewModel)此时读 _settings 已无 UI 副作用。
            afterShown?.Invoke();
        };
        timer.Start();
    }
}
