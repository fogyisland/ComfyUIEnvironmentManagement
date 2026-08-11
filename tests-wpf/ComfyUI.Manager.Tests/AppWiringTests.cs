using System;
using System.IO;
using System.Net.Http;
using ComfyUI.Manager;
using ComfyUI.Manager.Data;
using Xunit;

namespace ComfyUI.Manager.Tests;

/// <summary>
/// Composition/wiring regression tests for <see cref="App"/>.
///
/// T7 只关心"App 能不能把 <see cref="PyTorchVersionDirectory"/> 组装出来
/// 并交给 MainViewModel/EnvironmentListViewModel",不关心目录内容 —— 内容由
/// <c>PyTorchVersionDirectoryTests</c> 覆盖。所以这里:
/// <list type="bullet">
/// <item>不启动 WPF(只调 <c>static</c> 方法,不 new <see cref="App"/>);</item>
/// <item>不发真实网络请求(只构造对象,不调 <c>GetEntriesAsync</c>);</item>
/// <item>不写磁盘(appDataDir 用临时路径,cache 是 lazy 的)。</item>
/// </list>
/// </summary>
public class AppWiringTests
{
    /// <summary>
    /// App 的组装 helper 必须返回一个可用的 <see cref="PyTorchVersionDirectory"/>,
    /// 且接受 App 共享的 15 秒超时 <see cref="HttpClient"/>(即 catalog 不自己
    /// new HttpClient,复用调用方传入的那份)。
    /// </summary>
    [Fact]
    public void BuildPyTorchVersionDirectory_ReturnsDirectory_UsingSharedHttpClient()
    {
        // App.OnStartup 里那份共享 client 的等价物(15s 超时)。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var appDataDir = Path.Combine(
            Path.GetTempPath(), "ComfyUI-Manager-Tests", Guid.NewGuid().ToString("N"));

        var directory = App.BuildPyTorchVersionDirectory(appDataDir, http);

        Assert.NotNull(directory);
        Assert.IsType<PyTorchVersionDirectory>(directory);
        // 组装本身不得触发 I/O:目录不该被提前创建。
        Assert.False(Directory.Exists(appDataDir));
        // 共享 client 未被 helper 释放/改写,仍是 App 设定的 15s。
        Assert.Equal(TimeSpan.FromSeconds(15), http.Timeout);
    }
}
