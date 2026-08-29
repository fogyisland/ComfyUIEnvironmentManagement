using System;
using System.Diagnostics;
using System.IO;
using System.Management;   // System.Management 8.0.0 nuget (net5+ 已分离出 BCL,见 csproj)
using System.Runtime.InteropServices;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x: 启动期 env 孤儿检测 — port→pid + pid 归属判定。
///
/// 设计决策(改用 heuristic 而非 P/Invoke):
/// - 原计划走 iphlpapi!GetExtendedTcpTable(已实现)+ NtQueryInformationProcess + ReadProcessMemory 读
///   RTL_USER_PROCESS_PARAMETERS.CurrentDirectory — 但 RTL_USER_PROCESS_PARAMETERS 在 x64 上
///   offset 不稳定(Windows 10/11 多次实测偏移到 MZ 头/heap 头),≥3 次试错无解。
/// - 改用 <see cref="IsEnvProcessOwned"/>:<see cref="Process.MainModule"/> 拿到 EXE 路径,
///   检查其所在目录是否在 <paramref name="envRootPath"/> 下。
/// - 覆盖率:6/7 built-in templates(ComfyUI/Forge/OpenVoice/Whisper/CoquiTTS/Bark)— 它们的
///   进程都是 venv 内的 python.exe,EXE 路径在 envRootPath/.venv/Scripts/python.exe。
/// - SwarmUI 已下线 (2026-08-29):以前的例外描述移除 — 现在所有 built-in 都走 venv python。
/// - 失败一律返 false,调用方 skip 而非 throw(启动期 cleanup 必须 fail-safe)。
///
/// 测试 seam:<see cref="EnvOrphanReaper"/> 把这两个方法包装成 Func 注入,
/// 测试不依赖真实 Win32 / Process / Process.GetProcessById。
/// </summary>
public static class EnvPortProbe
{
    /// <summary>
    /// 返回当前监听 <paramref name="port"/> 的进程 pid。null = 端口无人监听 / 平台不支持 / 查询失败。
    /// IPv4 + IPv6 都查(只看 localPort,不区分 family — env 默认 --listen 0.0.0.0)。
    /// </summary>
    public static int? GetListeningPidByPort(int port)
    {
        if (port <= 0 || port > 65535) return null;
        try
        {
            int? v4 = GetListeningPidByPortFamily(port, AF_INET);
            if (v4.HasValue) return v4;
            return GetListeningPidByPortFamily(port, AF_INET6);
        }
        catch
        {
            return null;
        }
    }

    private static int? GetListeningPidByPortFamily(int port, int family)
    {
        int bufSize = 0;
        // 第一次调用,返 ERROR_INSUFFICIENT_BUFFER(122)+ 写回所需 size — 预期行为。
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref bufSize, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (rc != 0 && rc != 122) return null;
        if (bufSize <= 0) return null;

        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref bufSize, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (rc != 0) return null;
            int count = Marshal.ReadInt32(buffer);
            if (count <= 0) return null;

            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                // dwLocalPort 是 ULONG,端口号存于**低 16 位**(网络字节序 / big-endian):
                // 在 little-endian host 上 memory bytes 是 [b0, b1, b2, b3];端口字节是 b0 (高) + b1 (低),
                // 整体在低 16 位。dwLocalPort & 0xFFFF 后 byte-swap 高低字节得端口号。
                // 验证(2026-08-28 dump):
                //   row[0] dwLocalPort=0x00000100 → byte-swap = 0x0001 = 1   (netstat port 1,pid 28360 ✓)
                //   row[1] dwLocalPort=0x00008700 → byte-swap = 0x0087 = 135 (netstat port 135,pid 2196 ✓)
                //   row[2] dwLocalPort=0x0000BD01 → byte-swap = 0x01BD = 445 (netstat port 445,pid 4 ✓)
                //   row[3] dwLocalPort=0x00009905 → byte-swap = 0x0599 = 1433 (netstat port 1433,pid 25924 ✓)
                //   row[4] dwLocalPort=0x00008308 → byte-swap = 0x0883 = 2179 (netstat port 2179,pid 4184 ✓)
                uint low16 = row.dwLocalPort & 0xFFFFu;
                int localPort = (int)(((low16 >> 8) & 0xFFu) | ((low16 & 0xFFu) << 8));
                if (localPort == port)
                {
                    return (int)row.dwOwningPid;
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// pid 进程是否属于 <paramref name="envRootPath"/> env?Windows NTFS 大小写不敏感。
    /// 判定规则(v1.0.0.x 双维度):
    /// 1. EXE 目录在 <paramref name="envRootPath"/> 下(7/8 built-in templates 覆盖)
    /// 2. EXE 是 shipped-portable-python(&lt;root&gt;/python/python.exe)**AND** CommandLine 引用 envRootPath
    /// 进程不存在 / 权限不足 / MainModule 抛 Win32Exception / WMI 失败 / 路径 null → false。
    /// 任意失败都返 false,绝不上抛(启动期 cleanup 路径必须 fail-safe)。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Process.GetProcessById(int)"/> + <see cref="ProcessModule.FileName"/> 比 P/Invoke
    /// NtQueryInformationProcess + ReadProcessMemory PEB.CurrentDirectory 简单一个数量级,且对
    /// 跨 Windows 版本(10 / 11 / server)鲁棒。
    /// </remarks>
    public static bool IsEnvProcessOwned(int pid, string envRootPath)
    {
        if (pid <= 0 || string.IsNullOrEmpty(envRootPath)) return false;

        string? exePath;
        try
        {
            exePath = (ExePathLookup is null)
                ? GetExePathByPid(pid)  // 默认实现,封装 MainModule
                : ExePathLookup(pid);
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrEmpty(exePath)) return false;
        var procDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(procDir)) return false;

        bool rule1 = PathStartsWith(procDir, envRootPath);
        // 规则 1:现有 — procDir 在 envRootPath 下
        if (rule1) return true;

        // 规则 2 (v1.0.0.x):EXE 是 shipped-portable-python + CommandLine 引用 envRootPath
        if (IsShippedPortablePython(exePath))
        {
            var cmdLine = GetProcessCommandLine(pid);
            if (!string.IsNullOrEmpty(cmdLine) && ContainsPath(cmdLine, envRootPath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// v1.0.0.x: 默认 EXE 路径查找 — Process.GetProcessById + MainModule。
    /// 失败(进程不存在 / 权限 / Win32Exception)→ null,绝不上抛。
    /// </summary>
    private static string? GetExePathByPid(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            // MainModule 在某些 process 上(已退出 / 32-bit on 64-bit / system)会抛 Win32Exception。
            return proc.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// shipped-portable-python 路径判定:EXE 路径末段 = python.exe 且父目录名 = "python"。
    /// 不依赖 projectRoot 反推(避免绝对路径硬编码),兼容 Windows + WSL。
    /// </summary>
    private static bool IsShippedPortablePython(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var fileName = Path.GetFileName(exePath);
        if (!string.Equals(fileName, "python.exe", StringComparison.OrdinalIgnoreCase)) return false;
        // 父目录必须是 "python"(兼容 Windows + WSL)
        var parentDir = Path.GetFileName(Path.GetDirectoryName(exePath)?.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(parentDir, "python", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 text 中查找路径(Windows 大小写不敏感 + 接受 path 后跟 \ / " ' 空白 或 text 结尾)。
    /// 避免 "D:\Envs\env1extra" 误匹配 envRoot="D:\Envs\env1"。
    /// </summary>
    internal static bool ContainsPath(string text, string path)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(path)) return false;
        var idx = text.IndexOf(path, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        // boundary check:path 后必须是 \ / " ' 空白 或 text 结尾
        var after = idx + path.Length;
        if (after >= text.Length) return true;
        char next = text[after];
        return next == Path.DirectorySeparatorChar
            || next == Path.AltDirectorySeparatorChar
            || next == '"' || next == '\'' || char.IsWhiteSpace(next);
    }

    /// <summary>
    /// Windows 路径前缀判定:归一化(absolute + 末尾 separator trim)+ Windows 大小写不敏感。
    /// 任一为 null/empty / 路径操作抛 → false。
    /// </summary>
    private static bool PathStartsWith(string path, string prefix)
    {
        try
        {
            var np = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var nq = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (np.Length < nq.Length) return false;
            // 必须 boundary match(避免 D:\Envs\env1extra 误判 D:\Envs\env1)
            if (!np.StartsWith(nq, StringComparison.OrdinalIgnoreCase)) return false;
            if (np.Length == nq.Length) return true;
            char next = np[nq.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v1.0.0.x: 测试 seam — pid → EXE 路径。null → 走 <see cref="GetExePathByPid"/> 默认实现
    /// (Process.GetProcessById + MainModule)。Reaper 在 ownerCheck 循环外临时设置,循环结束还原。
    /// </summary>
    internal static Func<int, string?>? ExePathLookup { get; set; }

    /// <summary>
    /// v1.0.0.x: 测试 seam — 替换 WMI lookup 让单测不依赖真 WMI。
    /// 默认指向 <see cref="DefaultGetProcessCommandLine"/>(static field init 时绑定,防递归)。
    /// </summary>
    internal static Func<int, string?> CommandLineLookup { get; set; } = DefaultGetProcessCommandLine;

    /// <summary>
    /// v1.0.0.x: 跨进程读 CommandLine — WMI Win32_Process.CommandLine。
    /// 失败(进程不存在 / 权限 / WMI down / 任何异常)→ null,绝不上抛。
    /// 启动期 Reaper 路径必须 fail-safe(line 21-22 fail-safe 原则)。
    /// </summary>
    public static string? GetProcessCommandLine(int pid)
    {
        if (pid <= 0) return null;
        var lookup = CommandLineLookup;
        try
        {
            return lookup(pid);
        }
        catch
        {
            return null;
        }
    }

    private static string? DefaultGetProcessCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                var cmd = obj["CommandLine"]?.ToString();
                if (!string.IsNullOrEmpty(cmd)) return cmd;
            }
        }
        catch
        {
            // 失败一律返 null — WMI down / 进程已退出 / 权限不足 / 任何异常都不上抛。
            // Reaper 必须 fail-safe,绝不让 WMI 异常阻断启动。
        }
        return null;
    }

    #region Win32 P/Invoke(port→pid only)

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int TableClass,
        uint Reserved);

    #endregion
}
