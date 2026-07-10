using System;
using System.Collections.ObjectModel;
using ComfyUI.Manager.Infrastructure;

namespace ComfyUI.Manager.ViewModels;

public enum ErrorSeverity { Info, Warn, Error, Critical }

public record ErrorBannerEntry(
    string Code, string Message, ErrorSeverity Severity, DateTime At);

public class ErrorBannerViewModel : ViewModelBase
{
    public ObservableCollection<ErrorBannerEntry> Entries { get; } = new();

    public void Add(string code, string message, ErrorSeverity severity)
    {
        Entries.Insert(0, new ErrorBannerEntry(
            code, message, severity, DateTime.Now));
        // 限制最多 20 条
        while (Entries.Count > 20)
            Entries.RemoveAt(Entries.Count - 1);
    }
}