using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.UI;

public partial class PlaybackWindow
{
    private long _requestedPlaybackRecordId;

    internal void RequestPlayRecord(long videoRecordId)
    {
        if (videoRecordId <= 0)
            return;
        Interlocked.Exchange(ref _requestedPlaybackRecordId, videoRecordId);
        if (IsLoaded && !_isClosing)
            RequestVideoLoad(1);
    }

    private async Task TryPlayRequestedRecordingAsync()
    {
        long recordId = Interlocked.Exchange(
            ref _requestedPlaybackRecordId,
            0);
        if (recordId <= 0 || _isClosing)
            return;

        VideoItem? item = _allVideos.FirstOrDefault(
            video => video.RecordId == recordId);
        if (item == null && _db != null)
        {
            try
            {
                VideoRecord? record = await Task.Run(
                    () => _db.GetVideoById(recordId));
                if (record != null)
                {
                    item = CreateVideoItem(
                        record,
                        _computerName,
                        _currentSourceDeviceNames,
                        _canDeleteRecords);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "Playback",
                    $"读取待播放录像失败 record={recordId}: {ex.Message}");
            }
        }

        if (item == null)
        {
            AppDialog.Information(
                this,
                AppLanguage.Get("这条录像暂时无法播放"),
                AppLanguage.Get("回放"));
            return;
        }

        if (!item.IsPhoto && _mediaKind == "photo")
        {
            _mediaKind = "all";
            UpdateMediaKindButtons();
            RefreshFilterIndicators();
        }
        if (!_allVideos.Contains(item))
        {
            _allVideos.Insert(0, item);
            _totalVideos = Math.Max(_totalVideos + 1, _allVideos.Count);
        }
        ShowCurrentPage();
        if (ReferenceEquals(VideoList.SelectedItem, item))
            PlaySelectedVideo(item);
        else
            VideoList.SelectedItem = item;
    }
}
