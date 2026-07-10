using System;
using System.Windows;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    public ApiClient Api { get; }
    public WsClient Ws { get; }
    public PythonLauncher Launcher { get; }
    public ErrorBannerViewModel ErrorBanner { get; } = new();

    public MainViewModel(ApiClient api, WsClient ws,
        PythonLauncher launcher)
    {
        Api = api;
        Ws = ws;
        Launcher = launcher;
        // 订阅 WS errorOccurred 事件
        Ws.OnMessage += async msg =>
        {
            if (msg.Channel == "errorOccurred" && msg.Args.Length >= 2)
            {
                var code = msg.Args[0].GetString() ?? "UNKNOWN";
                var message = msg.Args[1].GetString() ?? "";
                var severity = SeverityFromCode(code);
                await DispatcherHelper.RunOnUiAsync(() =>
                    ErrorBanner.Add(code, message, severity));
            }
        };
    }

    private static ErrorSeverity SeverityFromCode(string code)
    {
        if (code.StartsWith("INTERNAL") || code.StartsWith("DB_"))
            return ErrorSeverity.Critical;
        if (code.StartsWith("BIZ_") || code.EndsWith("NOT_FOUND"))
            return ErrorSeverity.Warn;
        if (code.StartsWith("HTTP_") || code.StartsWith("WS_"))
            return ErrorSeverity.Warn;
        return ErrorSeverity.Error;
    }
}