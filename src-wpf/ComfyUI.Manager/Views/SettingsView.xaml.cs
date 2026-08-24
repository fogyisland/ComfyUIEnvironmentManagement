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

    private void BrowseSystemTemplateLibraryDir(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var picked = vm.PickFolder();
            if (picked is not null) vm.SystemTemplateLibraryDir = picked;
        }
    }

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
}
