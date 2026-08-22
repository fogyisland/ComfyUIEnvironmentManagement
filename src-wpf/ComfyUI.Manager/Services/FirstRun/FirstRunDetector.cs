using System.IO;

namespace ComfyUI.Manager.Services.FirstRun;

public static class FirstRunDetector
{
    public const string SentinelFileName = ".first-run-complete";
    public const string SettingsFileName = "settings.json";

    public static bool IsFirstRun(string appDataDir)
    {
        var sentinel = Path.Combine(appDataDir, SentinelFileName);
        if (File.Exists(sentinel)) return false;
        return true;
    }

    public static void MarkComplete(string appDataDir)
    {
        Directory.CreateDirectory(appDataDir);
        File.WriteAllText(Path.Combine(appDataDir, SentinelFileName), "");
    }
}
