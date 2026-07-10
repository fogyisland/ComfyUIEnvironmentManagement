using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

public class LogLine
{
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

public class LogViewerViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WsClient _ws;
    private readonly string _envId;
    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    public ObservableCollection<LogLine> Lines { get; } = new();
    public RelayCommand ClearCommand { get; }

    public LogViewerViewModel(ApiClient api, WsClient ws, string envId)
    {
        _api = api; _ws = ws; _envId = envId;
        ClearCommand = new RelayCommand(_ => Lines.Clear());
        _flushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        _ws.OnMessage += msg =>
        {
            if (msg.Channel == "logLine"
                && msg.Args.Length >= 2
                && msg.Args[0].GetString() == _envId)
            {
                var text = msg.Args[1].GetString() ?? "";
                _pending.Enqueue(new LogLine { Text = text, At = DateTime.Now });
            }
            return Task.CompletedTask;
        };

        _ = LoadHistoryAsync();
    }

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var batch = new List<LogLine>();
        while (_pending.TryDequeue(out var line))
            batch.Add(line);
        foreach (var line in batch) Lines.Add(line);
        // 限制最多 500 行
        while (Lines.Count > 500)
            Lines.RemoveAt(0);
    }

    private async Task LoadHistoryAsync()
    {
        var r = await _api.PostAsync<List<string>>(
            "process/logs-for", new { env_id = _envId });
        if (r.Ok && r.Value is not null)
        {
            foreach (var line in r.Value)
                Lines.Add(new LogLine { Text = line, At = DateTime.Now });
        }
    }
}