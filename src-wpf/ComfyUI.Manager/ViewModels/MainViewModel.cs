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
    // TODO(M5.2-T4): migrate to repository-based constructor — replace
    // ApiClient/WsClient with EnvironmentRepository/SettingsRepository/etc.
    // These are nullable for now because App.xaml.cs no longer launches the
    // Python control service; everything will be wired through repositories
    // in Task 4.
#pragma warning disable CS8625
    public ApiClient? Api { get; }
    public WsClient? Ws { get; }
#pragma warning restore CS8625
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

    public MainViewModel(ApiClient? api = null, WsClient? ws = null)
    {
        Api = api;
        Ws = ws;

        ShowEnvironmentsCommand = new RelayCommand(_ => CurrentView = null);
        ShowCatalogCommand = new RelayCommand(_ => CurrentView = null);
        ShowSettingsCommand = new RelayCommand(_ => CurrentView = null);
        OpenBulkUpdateCommand = new RelayCommand(_ => OpenBulkUpdate());

        if (Ws is not null)
        {
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
        // TODO(M5.2-T6): rewrite BulkUpdateDialog on top of
        // BulkUpdateOrchestrator (local C# orchestrator). The HTTP/WS path
        // is removed when the Python control service is deleted in T9.
        if (Api is null || Ws is null)
        {
            // Service disabled in M5.2 — bulk update UI not available yet.
            return;
        }

        var bulkApi = new BulkUpdateApiClient(Api.BaseUrl);
        var vm = new BulkUpdateDialogViewModel(bulkApi);

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
                else if (msg.Channel == "bulk_update.failed")
                {
                    var reason = msg.Args.Length >= 1
                        && msg.Args[0].ValueKind == JsonValueKind.String
                        ? msg.Args[0].GetString()
                        : null;
                    vm.ErrorMessage = reason ?? "bulk update failed";
                    vm.Mode = BulkUpdateMode.Summary;
                }
            });
        }
        Ws.OnMessage += Handler;

        BulkUpdateDialog.Show(vm);

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