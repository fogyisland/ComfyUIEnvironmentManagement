using System.Reflection;

namespace ComfyUI.Manager;

/// <summary>
/// v0.6.8: Splash 用的 version 字符串 helper。读 entry assembly 的
/// Version 字段,format 3 段(major.minor.build);若 null fallback "v?"
/// (理论上不会发生 — csproj 总是有 Version 字段)。
/// </summary>
public static class AppVersionInfo
{
    public static string Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "v?" : $"v{v.ToString(3)}";
        }
    }
}
