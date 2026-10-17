using ChangJing.App.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChangJing.App.ViewModels;

/// <summary>进度窗里的一个任务：源 → 目标、文件名、进度与暂停取消</summary>
public sealed partial class OperationTaskViewModel : ObservableObject
{
    private readonly OperationJob _job;
    private readonly Action<OperationTaskViewModel> _requestClose;

    public OperationTaskViewModel(
        OperationJob job,
        string sourceSummary,
        string targetDisplayPath,
        Action<OperationTaskViewModel> requestClose)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));

        KindText = job.Kind switch
        {
            OperationKind.Move => "移动",
            OperationKind.Delete => "删除",
            _ => "复制",
        };
        SourceSummary = sourceSummary;
        TargetDisplayPath = targetDisplayPath;
        RouteText = job.Kind == OperationKind.Delete || string.IsNullOrEmpty(targetDisplayPath)
            ? sourceSummary
            : $"{sourceSummary}  →  {targetDisplayPath}";
        State = job.State;
    }

    // 静态文本

    public string KindText { get; }

    public string SourceSummary { get; }

    /// <summary>目标目录普通路径</summary>
    public string TargetDisplayPath { get; }

    public string RouteText { get; }

    // 进度

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning))]
    [NotifyPropertyChangedFor(nameof(IsRunningOrPaused))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(PauseResumeText))]
    [NotifyPropertyChangedFor(nameof(CanPauseResume))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private OperationState _state;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _completedFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _totalFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    private long _completedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    private long _totalBytes;

    /// <summary>速度文本；无法计算时为空</summary>
    [ObservableProperty]
    private string _speedText = string.Empty;

    /// <summary>剩余时间文本；无法估算时为空</summary>
    [ObservableProperty]
    private string _remainingText = string.Empty;

    public bool IsScanning => State == OperationState.Scanning;

    public bool IsRunningOrPaused => State is OperationState.Running or OperationState.Paused;

    public bool IsPaused => State == OperationState.Paused;

    public string PauseResumeText => IsPaused ? "继续" : "暂停";

    public bool CanPauseResume => !IsFinished;

    public string PercentText => $"{Math.Round(Percent):0}%";

    public string CountText => $"{CompletedFiles} / {TotalFiles} 项";

    /// <summary>「已复制 X / 共 Y」文本；无字节信息时为空</summary>
    public string BytesText => TotalBytes > 0 || CompletedBytes > 0
        ? $"{ProgressVerb} {FileItemViewModel.FormatSize((ulong)Math.Max(0, CompletedBytes))} / 共 {FileItemViewModel.FormatSize((ulong)Math.Max(0, TotalBytes))}"
        : string.Empty;

    private string ProgressVerb => KindText switch
    {
        "移动" => "已移动",
        "删除" => "已删除",
        _ => "已复制",
    };

    public string StatusText => State switch
    {
        OperationState.Scanning => "正在计算…",
        OperationState.Paused => "已暂停",
        OperationState.Canceled => "已取消",
        OperationState.Failed => "失败",
        OperationState.Completed => "已完成",
        _ => string.Empty,
    };

    // 结束状态

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDismiss))]
    private bool _isFinished;

    [ObservableProperty]
    private bool _hasErrors;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>结束结果；未结束时为 <c>null</c></summary>
    public OperationResult? Result { get; private set; }

    // 进度应用

    public void ApplyProgress(OperationProgress progress)
    {
        // 任务结束后可能仍有排队的进度回调，忽略以免覆盖结束状态
        if (IsFinished)
        {
            return;
        }

        State = progress.State;
        Percent = progress.Percent;
        CompletedFiles = progress.CompletedFiles;
        TotalFiles = progress.TotalFiles;
        CompletedBytes = progress.CompletedBytes;
        TotalBytes = progress.TotalBytes;
        CurrentFileName = progress.CurrentItemName ?? string.Empty;

        SpeedText = progress.BytesPerSecond > 1
            ? FormatSpeed(progress.BytesPerSecond)
            : string.Empty;

        RemainingText = progress.EstimatedRemaining is { } remaining && remaining > TimeSpan.Zero
            ? $"剩余约 {FormatDuration(remaining)}"
            : string.Empty;
    }

    /// <summary>任务结束时由协调器调用</summary>
    public void ApplyFinished(OperationResult result)
    {
        Result = result;
        State = result.State;
        IsFinished = true;
        HasErrors = result.Errors.Count > 0;
        Percent = result.Success ? 100 : Percent;

        SummaryText = BuildSummaryText(result);

        if (result.Errors.Count > 0)
        {
            CurrentFileName = string.Empty;
            RemainingText = string.Empty;
            SpeedText = string.Empty;
        }
    }

    /// <summary>结束汇总：成功 / 部分失败 / 失败，并附带首条失败原因</summary>
    private string BuildSummaryText(OperationResult result)
    {
        string text;
        if (result.Canceled)
        {
            text = result.Message;
        }
        else if (result.Success)
        {
            text = $"已完成{KindText}：成功 {result.SucceededCount} 项。";
        }
        else if (result.Errors.Count > 0)
        {
            text = $"部分失败：{KindText}成功 {result.SucceededCount} 项，失败 {result.Errors.Count} 项。";
        }
        else
        {
            text = result.Message;
        }

        if (result.Errors.Count > 0)
        {
            OperationError first = result.Errors[0];
            text += $" 例：{first.Name} — {first.Reason}";
        }

        return text;
    }

    // 命令

    [RelayCommand]
    private void PauseResume()
    {
        if (_job.IsFinished)
        {
            return;
        }

        // 以底层任务的真实状态为准，界面状态可能滞后一个进度周期
        if (_job.State == OperationState.Paused)
        {
            _job.Resume();
        }
        else
        {
            _job.Pause();
        }

        State = _job.State;
    }

    [RelayCommand]
    private void Cancel()
    {
        _job.Cancel();
        State = _job.State;
    }

    [RelayCommand]
    private void ViewErrors()
    {
        if (Result is null)
        {
            return;
        }

        ViewErrorsRequested?.Invoke(this, Result);
    }

    /// <summary>点击「查看失败项」时触发，携带结束结果</summary>
    public event EventHandler<OperationResult>? ViewErrorsRequested;

    public bool CanDismiss => IsFinished;

    [RelayCommand]
    private void Dismiss() => RequestClose();

    /// <summary>供协调器在成功后从列表移除本行</summary>
    public void RequestClose() => _requestClose(this);

    // 格式化

    private static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytesPerSecond;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}/s";
    }

    public static string FormatDuration(TimeSpan value)
    {
        if (value.TotalSeconds < 1)
        {
            return "不到 1 秒";
        }

        if (value.TotalMinutes < 1)
        {
            return $"{Math.Ceiling(value.TotalSeconds):0} 秒";
        }

        if (value.TotalHours < 1)
        {
            int minutes = (int)value.TotalMinutes;
            int seconds = value.Seconds;
            return seconds == 0 ? $"{minutes} 分" : $"{minutes} 分 {seconds} 秒";
        }

        int hours = (int)value.TotalHours;
        int remainingMinutes = value.Minutes;
        return remainingMinutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {remainingMinutes} 分";
    }
}
