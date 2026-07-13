using System;
using System.Collections.ObjectModel;

namespace ComfyUI.Manager.ViewModels;

public class LogLine
{
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

public class LogViewerViewModel : ViewModelBase
{
    private readonly string _envId;

    public ObservableCollection<LogLine> Lines { get; } = new();
    public RelayCommand ClearCommand { get; }

    public LogViewerViewModel(string envId)
    {
        _envId = envId;
        ClearCommand = new RelayCommand(_ => Lines.Clear());
        // TODO(M5.2-T5): tail logs/<env-id>.log via LogTailer instead of the
        // removed WS push. Until T5 lands, the viewer starts empty.
    }
}
