using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    private string _archiveBackupShortStatusText = "检查中";
    private string _archiveBackupStatusText = "正在检查待备份录像";
    private bool _archiveBackupCardVisible;
    private int _remainingArchiveBackupCount;
    private bool _archiveTargetUnavailable;
    private string _archiveUnavailableRoot = "";
    private int _archiveBackupSummaryDirty;

    public bool IsArchiveBackupCardVisible
    {
        get => _archiveBackupCardVisible;
        private set
        {
            _archiveBackupCardVisible = value;
            OnPropertyChanged();
        }
    }

    public string ArchiveBackupShortStatusText
    {
        get => _archiveBackupShortStatusText;
        private set
        {
            _archiveBackupShortStatusText = value;
            OnPropertyChanged();
        }
    }

    public string ArchiveBackupStatusText
    {
        get => _archiveBackupStatusText;
        private set
        {
            _archiveBackupStatusText = value;
            OnPropertyChanged();
        }
    }

    public int RemainingArchiveBackupCount
    {
        get => _remainingArchiveBackupCount;
        private set
        {
            _remainingArchiveBackupCount = value;
            OnPropertyChanged();
        }
    }

    private void RefreshArchiveBackupSummary()
    {
        bool visible = ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            Config,
            IsRecordingWorkstation);
        IsArchiveBackupCardVisible = visible;
        if (!visible)
            return;

        ArchiveQueueSummary summary = _db?.GetArchiveQueueSummary()
            ?? new ArchiveQueueSummary(0, 0, 0, 0, 0, 0, 0, 0, 0);
        RemainingArchiveBackupCount = summary.RemainingCount;
        ArchiveBackupCardState state = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            summary,
            ArchiveBackupCardModel.ResolveCurrentArchiveTarget(Config),
            _archiveTargetUnavailable,
            _archiveUnavailableRoot,
            _archiveService?.CurrentWorkerSnapshot ?? default);
        ArchiveBackupShortStatusText = state.ShortStatusText;
        ArchiveBackupStatusText = state.DetailText;
    }

    private void OnArchiveWorkerStateChanged(ArchiveWorkerSnapshot _) =>
        Interlocked.Exchange(ref _archiveBackupSummaryDirty, 1);

    private void OnArchiveQueueChanged() =>
        Interlocked.Exchange(ref _archiveBackupSummaryDirty, 1);

    private void OnArchiveTargetAvailabilityChanged(bool available, string root)
    {
        _ = Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed)
                return;

            _archiveTargetUnavailable = !available;
            _archiveUnavailableRoot = available ? "" : root;
            RefreshArchiveBackupSummary();

            if (available)
                ShowToast("网络备份位置已恢复，继续备份录像", ToastSeverity.Information);
            else
                ShowToast("网络备份位置不可用，录像保留在本地，恢复后自动重试", ToastSeverity.Warning);
        });
    }
}
