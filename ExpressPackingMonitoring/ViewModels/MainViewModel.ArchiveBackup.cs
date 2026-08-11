using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    private string _archiveBackupShortStatusText = "检查中";
    private string _archiveBackupStatusText = "正在检查待备份录像";
    private bool _archiveBackupCardVisible;
    private int _remainingArchiveBackupCount;

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
            ?? new ArchiveQueueSummary(0, 0, 0, 0);
        RemainingArchiveBackupCount = summary.RemainingCount;
        ArchiveBackupCardState state = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            summary.PendingCount,
            summary.UploadingCount,
            summary.FailedCount,
            summary.NasFullCount,
            ArchiveBackupCardModel.ResolveCurrentArchiveTarget(Config));
        ArchiveBackupShortStatusText = state.ShortStatusText;
        ArchiveBackupStatusText = state.DetailText;
    }
}
