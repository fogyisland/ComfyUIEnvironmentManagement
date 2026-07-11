using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Views;

namespace ComfyUI.Manager.ViewModels;

public class MainViewModel : ViewModelBase
{
    public ApiClient Api { get; }
    public WsClient Ws { get; }
    public PythonLauncher Launcher { get; }
    public ErrorBannerViewModel ErrorBanner { get; } = new();

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    public ICommand ShowEnvironmentsCommand { get; }
    public ICommand ShowCatalogCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand OpenBulkUpdateCommand { get; }

    public MainViewModel(ApiClient api, WsClient ws,
        PythonLauncher launcher)
    {
        Api = api;
        Ws = ws;
        Launcher = launcher;

        ShowEnvironmentsCommand = new RelayCommand(_ => CurrentView = null);
        ShowCatalogCommand = new RelayCommand(_ => CurrentView = null);
        ShowSettingsCommand = new RelayCommand(_ => CurrentView = null);
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());

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

    private void OpenBulkUpdate()
    {
        var bulkApi = new BulkUpdateApiClient(Api.BaseUrl);
        var vm = new BulkUpdateDialogViewModel(bulkApi);

        // Subscribe to WS channel bulk_update.* for this dialog's lifetime
        async Task Handler(WsMessage msg)
        {
            if (msg.Channel != "bulk_update.progress"
                && msg.Channel != "bulk_update.completed"
                && msg.Channel != "bulk_update.cancelled"
                && msg.Channel != "bulk_update.failed")
                return;

            await DispatcherHelper.RunOnUiAsync(() =>
            {
                if (msg.Channel == "bulk_update.progress" && msg.Args.Length >= 1)
                {
                    var p = msg.Args[0];
                    vm.UpdateRow(
                        p.GetProperty("env_id").GetString() ?? "",
                        p.GetProperty("node_id").GetString() ?? "",
                        p.GetProperty("status").GetString() ?? "failed",
                        p.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                        p.TryGetProperty("latency_ms", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0);
                }
                else if (msg.Channel is "bulk_update.completed" or "bulk_update.cancelled")
                {
                    var s = msg.Args[0];
                    var summary = new BulkUpdateSummary(
                        s.GetProperty("total").GetInt32(),
                        s.GetProperty("succeeded").GetInt32(),
                        s.GetProperty("skipped").GetInt32(),
                        s.GetProperty("failed").GetInt32(),
                        ParseRows(s.GetProperty("rows")));
                    vm.SetSummary(summary);
                }
                // failed: just ignore for now (could set ErrorMessage on vm)
            });
        }
        Ws.OnMessage += Handler;

        BulkUpdateDialog.Show(vm);

        // Cleanup subscription when dialog closes
        Ws.OnMessage -= Handler;
    }

    private static List<BulkUpdateRow> ParseRows(JsonElement rowsEl)
    {
        var list = new List<BulkUpdateRow>();
        foreach (var r in rowsEl.EnumerateArray())
        {
            list.Add(new BulkUpdateRow(
                r.GetProperty("env_id").GetString() ?? "",
                r.GetProperty("node_id").GetString() ?? "",
                r.GetProperty("status").GetString() ?? "failed",
                r.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null,
                r.TryGetProperty("latency_ms", out var la) && la.ValueKind == JsonValueKind.Number ? la.GetInt32() : 0));
        }
        return list;
    }
}
