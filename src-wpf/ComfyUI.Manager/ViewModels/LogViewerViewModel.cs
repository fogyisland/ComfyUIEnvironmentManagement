using System;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

public class LogLine
{
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

public class LogViewerViewModel : ViewModelBase, IDisposable
{
    private const int MaxLines = 500;
    private readonly LogTailer _tailer;

    public ObservableCollection<LogLine> Lines { get; } = new();
    public RelayCommand ClearCommand { get; }

    public string EnvId { get; }

    public LogViewerViewModel(string envId, LogTailer tailer)
    {
        if (string.IsNullOrWhiteSpace(envId))
            throw new ArgumentException("envId 不能为空", nameof(envId));
        EnvId = envId;
        _tailer = tailer ?? throw new ArgumentNullException(nameof(tailer));

        ClearCommand = new RelayCommand(_ => Lines.Clear());

        _tailer.NewLine += OnNewLine;
        _tailer.Start();
    }

    private void OnNewLine(LogLine line)
    {
        // tailer 在后台线程跑,得切到 UI 线程写 ObservableCollection
        _ = DispatcherHelper.RunOnUiAsync(() => AppendLine(line));
    }

    private void AppendLine(LogLine line)
    {
        Lines.Add(line);
        // cap at MaxLines —— 删最旧的
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        try { _tailer.NewLine -= OnNewLine; } catch { }
        try { _tailer.Dispose(); } catch { }
    }
}
