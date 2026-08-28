using System;
using System.Diagnostics;
using System.IO;
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
/// - 覆盖率:7/8 built-in templates(ComfyUI/A1111/Forge/OpenVoice/Whisper/CoquiTTS/Bark)— 它们的
///   进程都是 venv 内的 python.exe,EXE 路径在 envRootPath/.venv/Scripts/python.exe。
/// - SwarmUI 例外:SwarmUI 是 cmd.exe + bat 包装,MainModule 给的是 C:\Windows\System32\cmd.exe,
///   不在 envRootPath 下,会被误判为"非本 env"→ 不 reap(用户手动 stop,acceptable 退化)。
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
                // dwLocalPort 是 ULONG,实际只用 16 bits,按 network byte order 存。
                // x86/x64 little-endian 上:读成 uint 后高 16 位是有效端口字节。
                int localPort = (int)((row.dwLocalPort & 0xFFFF0000u) >> 16);
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
    /// pid 进程的 EXE 目录是否在 <paramref name="envRootPath"/> 下?Windows NTFS 大小写不敏感。
    /// 进程不存在 / 权限不足 / MainModule 抛 Win32Exception / 路径 null → false。
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
        try
        {
            using var proc = Process.GetProcessById(pid);
            // MainModule 在某些 process 上(已退出 / 32-bit on 64-bit / system)会抛 Win32Exception。
            var exePath = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return false;
            var procDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(procDir)) return false;
            return PathStartsWith(procDir, envRootPath);
        }
        catch
        {
            return false;
        }
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
