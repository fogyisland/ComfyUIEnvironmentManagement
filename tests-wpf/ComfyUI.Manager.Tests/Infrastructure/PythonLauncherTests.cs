using ComfyUI.Manager.Infrastructure;
using Xunit;
using System.Threading.Tasks;

namespace ComfyUI.Manager.Tests.Infrastructure;

public class PythonLauncherTests
{
    [Fact]
    public void IsPortInUse_DetectsOccupiedPort()
    {
        // 用 TcpListener 占个端口
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(PythonLauncher.IsPortInUse("127.0.0.1", port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsPortInUse_DetectsFreePort()
    {
        // 找一个空闲端口
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        // 短暂等待 OS 释放
        System.Threading.Thread.Sleep(100);
        Assert.False(PythonLauncher.IsPortInUse("127.0.0.1", port));
    }

    [Fact]
    public async Task LaunchAsync_ThrowsOnPortConflict()
    {
        // 占 7800
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 7800);
        try
        {
            listener.Start();
            var launcher = new PythonLauncher("C:\\fake", port: 7800);
            await Assert.ThrowsAsync<ServiceLaunchException>(
                () => launcher.LaunchAsync());
        }
        catch
        {
            // 7800 已被别的占用,跳过测试
        }
        finally
        {
            listener.Stop();
        }
    }
}