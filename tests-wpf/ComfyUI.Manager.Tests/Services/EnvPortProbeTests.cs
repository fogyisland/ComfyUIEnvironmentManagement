using System;
using System.Diagnostics;
using System.IO;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x: EnvPortProbe 单元测试 — 只测静态方法的"输入参数 sanity → 输出 null/false"。
/// 真实 Win32 P/Invoke(iphlpapi / Process)测试在 STA + 真实 listener 下不稳,
/// 留给 smoke / dev build 验证。这里只保证参数 sanity 不抛 + 越界返 null/false。
/// </summary>
public class EnvPortProbeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void GetListeningPidByPort_InvalidPort_ReturnsNull(int port)
    {
        Assert.Null(EnvPortProbe.GetListeningPidByPort(port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public void IsEnvProcessOwned_InvalidPid_ReturnsFalse(int pid)
    {
        Assert.False(EnvPortProbe.IsEnvProcessOwned(pid, @"D:\Envs\env1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsEnvProcessOwned_NullOrEmptyRootPath_ReturnsFalse(string root)
    {
        Assert.False(EnvPortProbe.IsEnvProcessOwned(Environment.ProcessId, root));
    }

    [Fact]
    public void GetListeningPidByPort_ValidPortButNothingListening_ReturnsNull()
    {
        // 静态 smoke:挑一个极不可能有人在听的端口(高位,无 well-known service)。
        // 如果本机恰好有进程占了这个端口,测试会失败 — 此时应换端口或跳过。
        // 当前默认 0xC000 = 49152,在测试环境空闲。
        Assert.Null(EnvPortProbe.GetListeningPidByPort(49152));
    }

    [Fact]
    public void IsEnvProcessOwned_CurrentProcessInsideRoot_ReturnsTrue()
    {
        // 当前测试进程的 EXE 必须在它自己的 bin 目录下 → 拿 bin 目录的 grandparent 当 root。
        var exePath = Process.GetCurrentProcess().MainModule!.FileName!;
        var exeDir = Path.GetDirectoryName(exePath)!;
        // exeDir 通常是 .../bin/Debug/net8.0-windows/ → grandparent 是 .../bin/Debug/
        var probeRoot = Directory.GetParent(exeDir)?.Parent?.FullName;
        if (probeRoot is null)
        {
            // 单层目录(testhost 直挂 project)— 退化为父目录。
            probeRoot = Directory.GetParent(exeDir)!.FullName;
        }
        Assert.True(EnvPortProbe.IsEnvProcessOwned(Environment.ProcessId, probeRoot));
    }

    [Fact]
    public void IsEnvProcessOwned_CurrentProcessOutsideRoot_ReturnsFalse()
    {
        // 拿一个肯定不相关的目录当 root,确认不会误判。
        var unrelatedRoot = @"C:\DefinitelyNotTheTestHost\Sub";
        Assert.False(EnvPortProbe.IsEnvProcessOwned(Environment.ProcessId, unrelatedRoot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetProcessCommandLine_InvalidPid_ReturnsNull(int pid)
    {
        Assert.Null(EnvPortProbe.GetProcessCommandLine(pid));
    }

    [Fact]
    public void GetProcessCommandLine_NonexistentPid_ReturnsNull()
    {
        // 极不可能存在的 pid(高位 + 已知 dead 区间)
        Assert.Null(EnvPortProbe.GetProcessCommandLine(999_999_999));
    }

    [Fact]
    public void CommandLineLookup_CanBeOverridden_ReturnsInjectedValue()
    {
        var prev = EnvPortProbe.CommandLineLookup;
        try
        {
            EnvPortProbe.CommandLineLookup = _ => "fake cmdline";
            Assert.Equal("fake cmdline", EnvPortProbe.GetProcessCommandLine(1));
        }
        finally
        {
            EnvPortProbe.CommandLineLookup = prev;
        }
    }

    [Fact]
    public void GetProcessCommandLine_LookupThrows_ReturnsNull()
    {
        var prev = EnvPortProbe.CommandLineLookup;
        try
        {
            EnvPortProbe.CommandLineLookup = _ => throw new InvalidOperationException("WMI down");
            Assert.Null(EnvPortProbe.GetProcessCommandLine(1));
        }
        finally
        {
            EnvPortProbe.CommandLineLookup = prev;
        }
    }
}
