using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.UI
{
    public partial class PlaybackWindow
    {
        private const double PhotoPreviewMaxScale = 8.0;
        private const double PhotoPreviewZoomStep = 1.2;
        private bool _photoPreviewDragging;
        private Point _photoPreviewDragStart;
        private double _photoPreviewDragStartX;
        private double _photoPreviewDragStartY;

        private void VideoMediaTabButton_Click(object sender, RoutedEventArgs e) =>
            SetMediaKind("video");

        private void PhotoMediaTabButton_Click(object sender, RoutedEventArgs e) =>
            SetMediaKind("photo");

        private void AllMediaTabButton_Click(object sender, RoutedEventArgs e) =>
            SetMediaKind("all");

        private void SetMediaKind(string mediaKind)
        {
            string normalized = mediaKind switch
            {
                "photo" => "photo",
                "video" => "video",
                _ => "all"
            };
            if (normalized != "video" && _photoStore == null)
                normalized = "video";
            if (_mediaKind == normalized)
                return;
            _mediaKind = normalized;
            VideoList.SelectedItem = null;
            HidePhotoPreview();
            UpdateMediaKindButtons();
            RefreshFilterIndicators();
            RequestVideoLoad(1);
        }

        private void UpdateMediaKindButtons()
        {
            var activeBackground = (Brush)FindResource("AccentBlue");
            var idleBackground = (Brush)FindResource("ControlBackgroundHover");
            var activeForeground = (Brush)FindResource("TextOnAccent");
            var idleForeground = (Brush)FindResource("TextPrimary");
            bool allActive = _mediaKind == "all";
            bool videoActive = _mediaKind == "video";
            bool photoActive = _mediaKind == "photo";
            AllMediaTabButton.Background =
                allActive ? activeBackground : idleBackground;
            AllMediaTabButton.Foreground =
                allActive ? activeForeground : idleForeground;
            VideoMediaTabButton.Background =
                videoActive ? activeBackground : idleBackground;
            VideoMediaTabButton.Foreground =
                videoActive ? activeForeground : idleForeground;
            PhotoMediaTabButton.Background =
                photoActive ? activeBackground : idleBackground;
            PhotoMediaTabButton.Foreground =
                photoActive ? activeForeground : idleForeground;
            ExportOrderNumbersButton.Visibility = photoActive
                ? Visibility.Collapsed
                : Visibility.Visible;
            BtnImportVideos.Visibility =
                !photoActive && _videoImportService != null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private VideoPageLoadResult BuildCombinedPage(
            DateTime? start,
            DateTime? end,
            string? keyword,
            int page,
            string mode,
            string sourceType,
            string sourceId,
            string sourceName,
            IReadOnlyList<string>? sourceIds)
        {
            int requiredPerSource = page * PageSize;
            int sourcePageCount = Math.Max(
                1,
                (requiredPerSource + PageSize - 1) / PageSize);
            var videoItems = new List<VideoItem>();
            var photoItems = new List<VideoItem>();
            int videoTotal = 0;
            int photoTotal = 0;
            for (int sourcePage = 1; sourcePage <= sourcePageCount; sourcePage++)
            {
                VideoPageLoadResult videoPage = BuildVideoPage(
                    start,
                    end,
                    keyword,
                    sourcePage,
                    mode,
                    sourceType,
                    sourceId,
                    sourceName,
                    sourceIds,
                    forceVideoOnly: true);
                VideoPageLoadResult photoPage = BuildPhotoPage(
                    start,
                    end,
                    keyword,
                    sourcePage,
                    mode,
                    sourceType,
                    sourceId,
                    sourceName,
                    sourceIds);
                if (sourcePage == 1)
                {
                    videoTotal = videoPage.Total;
                    photoTotal = photoPage.Total;
                }
                videoItems.AddRange(videoPage.Items);
                photoItems.AddRange(photoPage.Items);
            }
            List<VideoItem> combined = videoItems
                .Concat(photoItems)
                .OrderByDescending(item => item.StartTime)
                .ThenByDescending(item => item.IsPhoto)
                .ThenByDescending(item => item.IsPhoto ? item.PhotoId : item.RecordId)
                .ToList();
            int total = videoTotal + photoTotal;
            return new VideoPageLoadResult(
                combined
                    .Skip((page - 1) * PageSize)
                    .Take(PageSize)
                    .ToList(),
                total,
                page * PageSize < total,
                false,
                page);
        }

        private VideoPageLoadResult BuildPhotoPage(
            DateTime? start,
            DateTime? end,
            string? keyword,
            int page,
            string mode,
            string sourceType,
            string sourceId,
            string sourceName,
            IReadOnlyList<string>? sourceIds)
        {
            if (_photoStore == null)
                return new VideoPageLoadResult([], 0, false, false, 1);
            if (string.Equals(
                    sourceType,
                    "external",
                    StringComparison.OrdinalIgnoreCase))
                return new VideoPageLoadResult([], 0, false, false, page);

            string normalizedMode = RecordingModeFilter.Normalize(mode);
            DateTimeOffset? startInclusive = start.HasValue
                ? new DateTimeOffset(start.Value.Date)
                : null;
            DateTimeOffset? endExclusive = end.HasValue
                ? new DateTimeOffset(end.Value.Date.AddDays(1))
                : null;
            PhotoQueryPage result = _photoStore.QueryPage(
                keyword ?? "",
                (page - 1) * PageSize,
                PageSize,
                startInclusive,
                endExclusive,
                normalizedMode,
                sourceId,
                sourceName,
                sourceIds);
            List<VideoItem> items = result.Items
                .Select(CreatePhotoItem)
                .ToList();
            return new VideoPageLoadResult(
                items,
                result.Total,
                page * PageSize < result.Total,
                false,
                page);
        }

        private VideoItem CreatePhotoItem(PhotoRecord record)
        {
            string tracking = string.IsNullOrWhiteSpace(record.TrackingNumber)
                ? AppLanguage.Get("未记录单号")
                : record.TrackingNumber;
            string archiveStatus = record.ArchiveStatus switch
            {
                "Completed" => AppLanguage.Get("已备份"),
                "Pending" => AppLanguage.Get("等待备份"),
                "Failed" => AppLanguage.Get("备份失败"),
                _ => ""
            };
            bool missing = !File.Exists(record.FilePath);
            return new VideoItem
            {
                IsPhoto = true,
                PhotoId = record.Id,
                PhotoArchiveStatus = archiveStatus,
                IsPhotoArchiveCompleted = record.ArchiveStatus == "Completed",
                DisplayName = tracking,
                FullPath = record.FilePath,
                FileName = Path.GetFileName(record.FilePath),
                OrderId = record.TrackingNumber,
                TrackingNumber = record.TrackingNumber,
                Mode = record.Mode,
                Duration = AppLanguage.Get("图片"),
                PhotoDimensions = $"{record.Width}×{record.Height}",
                FileSizeBytes = record.FileSizeBytes,
                FileSize = FormatFileSize(record.FileSizeBytes),
                SourceDisplay = string.IsNullOrWhiteSpace(record.SourceDeviceName)
                    ? AppLanguage.Get("本机")
                    : record.SourceDeviceName,
                SourceDeviceId = record.SourceDeviceId,
                StartTime = record.CreatedAt.LocalDateTime,
                ArchiveStatusText = archiveStatus,
                IsArchiveWarning = record.ArchiveStatus == "Failed",
                IsMissing = missing,
                CanDelete = _deletePhotoHandler != null,
                DeleteBlockedHint = _deletePhotoHandler == null
                    ? AppLanguage.Get("当前用途不能删除照片")
                    : ""
            };
        }


        internal void ApplyMixedLibraryPreviewForTest(
            VideoItem? video,
            IReadOnlyList<PhotoRecord> records,
            BitmapSource preview)
        {
            _mediaKind = "all";
            UpdateMediaKindButtons();
            _allVideos = records
                .Select(CreatePhotoItem)
                .Prepend(video)
                .Where(item => item != null)
                .Cast<VideoItem>()
                .OrderByDescending(item => item.StartTime)
                .ToList();
            _totalVideos = _allVideos.Count;
            ShowCurrentPage();
            PlayerView.Visibility = Visibility.Hidden;
            PlaybackCover.Visibility = Visibility.Collapsed;
            PhotoPreviewImage.Source = preview;
            ResetPhotoPreviewInteraction();
            PhotoPreviewViewport.Visibility = Visibility.Visible;
            PlaybackControlBar.Visibility = Visibility.Collapsed;
            TimeLabel.Text = _allVideos.FirstOrDefault()?.Duration ?? "";
        }


        private async Task ShowPhotoPreviewAsync(VideoItem video)
        {
            int requestVersion = 0;
            RuntimeLog.Info("PlaybackPhoto", "Photo preview start");
            try
            {
                await StopAndDisposePlaybackForDeletionAsync();
                RuntimeLog.Info("PlaybackPhoto", "Playback source released");
                if (VideoList.SelectedItem != video)
                    return;
                requestVersion = Interlocked.Increment(
                    ref _playbackRequestVersion);
                string path = video.FullPath;
                BitmapSource image = await Task.Run(() =>
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    using var stream = new MemoryStream(bytes, writable: false);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return (BitmapSource)bitmap;
                });
                RuntimeLog.Info(
                    "PlaybackPhoto",
                    $"Photo decoded {image.PixelWidth}x{image.PixelHeight}");
                if (requestVersion != Volatile.Read(ref _playbackRequestVersion)
                    || VideoList.SelectedItem != video)
                {
                    RuntimeLog.Info(
                        "PlaybackPhoto",
                        $"Photo preview abandoned request={requestVersion}, "
                        + $"current={Volatile.Read(ref _playbackRequestVersion)}");
                    return;
                }

                PlayerView.Visibility = Visibility.Hidden;
                PlaybackCover.Visibility = Visibility.Collapsed;
                PhotoPreviewImage.Source = image;
                ResetPhotoPreviewInteraction();
                PhotoPreviewViewport.Visibility = Visibility.Visible;
                PlaybackControlBar.Visibility = Visibility.Collapsed;
                BtnTogglePlay.IsEnabled = false;
                TimelineSlider.IsEnabled = false;
                _screenshotSourceReady = false;
                UpdateScreenshotButtonState();
                TimeLabel.Text = video.Duration;
                PlaybackCoverText.Text = video.DisplayName;
                RuntimeLog.Info("PlaybackPhoto", "Photo preview visible");
            }
            catch (Exception ex)
            {
                if (VideoList.SelectedItem != video
                    || (requestVersion != 0
                        && requestVersion != Volatile.Read(ref _playbackRequestVersion)))
                    return;
                HidePhotoPreview();
                AppDialog.Error(
                    this,
                    AppLanguage.Format("读取照片失败：{0}", ex.Message),
                    AppLanguage.Get("照片预览"));
            }
        }

        private void HidePhotoPreview()
        {
            ResetPhotoPreviewInteraction();
            PhotoPreviewImage.Source = null;
            PhotoPreviewViewport.Visibility = Visibility.Collapsed;
            PlaybackControlBar.Visibility = Visibility.Visible;
            if (PlaybackCover.Visibility == Visibility.Collapsed)
                PlaybackCover.Visibility = Visibility.Visible;
        }

        private void PhotoPreviewViewport_MouseWheel(
            object sender,
            MouseWheelEventArgs e)
        {
            if (PhotoPreviewImage.Source == null || e.Delta == 0)
                return;
            ApplyPhotoPreviewZoom(e.Delta, e.GetPosition(PhotoPreviewViewport));
            e.Handled = true;
        }

        private void ApplyPhotoPreviewZoom(int delta, Point pointer)
        {
            double currentScale = PhotoPreviewScale.ScaleX;
            double nextScale = Math.Clamp(
                currentScale * (delta > 0
                    ? PhotoPreviewZoomStep
                    : 1 / PhotoPreviewZoomStep),
                1,
                PhotoPreviewMaxScale);
            if (Math.Abs(nextScale - currentScale) < 0.001)
                return;

            double centerX = PhotoPreviewViewport.ActualWidth / 2;
            double centerY = PhotoPreviewViewport.ActualHeight / 2;
            double scaleRatio = nextScale / currentScale;
            PhotoPreviewTranslate.X =
                (pointer.X - centerX) * (1 - scaleRatio)
                + PhotoPreviewTranslate.X * scaleRatio;
            PhotoPreviewTranslate.Y =
                (pointer.Y - centerY) * (1 - scaleRatio)
                + PhotoPreviewTranslate.Y * scaleRatio;
            PhotoPreviewScale.ScaleX = nextScale;
            PhotoPreviewScale.ScaleY = nextScale;
            ClampPhotoPreviewTranslation();
            UpdatePhotoPreviewCursor();
        }

        private void PhotoPreviewViewport_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (PhotoPreviewImage.Source == null
                || PhotoPreviewScale.ScaleX <= 1.001)
                return;

            _photoPreviewDragging = true;
            _photoPreviewDragStart = e.GetPosition(PhotoPreviewViewport);
            _photoPreviewDragStartX = PhotoPreviewTranslate.X;
            _photoPreviewDragStartY = PhotoPreviewTranslate.Y;
            PhotoPreviewViewport.CaptureMouse();
            PhotoPreviewViewport.Cursor = Cursors.Hand;
            e.Handled = true;
        }

        private void PhotoPreviewViewport_MouseMove(
            object sender,
            MouseEventArgs e)
        {
            if (!_photoPreviewDragging
                || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point current = e.GetPosition(PhotoPreviewViewport);
            PanPhotoPreviewTo(
                _photoPreviewDragStartX + current.X - _photoPreviewDragStart.X,
                _photoPreviewDragStartY + current.Y - _photoPreviewDragStart.Y);
            e.Handled = true;
        }

        private void PanPhotoPreviewTo(double x, double y)
        {
            PhotoPreviewTranslate.X = x;
            PhotoPreviewTranslate.Y = y;
            ClampPhotoPreviewTranslation();
        }

        private void PhotoPreviewViewport_MouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (!_photoPreviewDragging)
                return;
            _photoPreviewDragging = false;
            PhotoPreviewViewport.ReleaseMouseCapture();
            UpdatePhotoPreviewCursor();
            e.Handled = true;
        }

        private void PhotoPreviewViewport_LostMouseCapture(
            object sender,
            MouseEventArgs e)
        {
            _photoPreviewDragging = false;
            UpdatePhotoPreviewCursor();
        }

        private void ResetPhotoPreviewInteraction()
        {
            _photoPreviewDragging = false;
            if (PhotoPreviewViewport.IsMouseCaptured)
                PhotoPreviewViewport.ReleaseMouseCapture();
            PhotoPreviewScale.ScaleX = 1;
            PhotoPreviewScale.ScaleY = 1;
            PhotoPreviewTranslate.X = 0;
            PhotoPreviewTranslate.Y = 0;
            UpdatePhotoPreviewCursor();
        }

        private void ClampPhotoPreviewTranslation()
        {
            double scale = PhotoPreviewScale.ScaleX;
            double maxX = Math.Max(
                0,
                PhotoPreviewViewport.ActualWidth * (scale - 1) / 2);
            double maxY = Math.Max(
                0,
                PhotoPreviewViewport.ActualHeight * (scale - 1) / 2);
            PhotoPreviewTranslate.X = Math.Clamp(
                PhotoPreviewTranslate.X,
                -maxX,
                maxX);
            PhotoPreviewTranslate.Y = Math.Clamp(
                PhotoPreviewTranslate.Y,
                -maxY,
                maxY);
        }

        private void UpdatePhotoPreviewCursor()
        {
            PhotoPreviewViewport.Cursor =
                PhotoPreviewScale.ScaleX > 1.001
                    ? Cursors.SizeAll
                    : Cursors.Arrow;
        }

        internal double PhotoPreviewScaleForTest =>
            PhotoPreviewScale.ScaleX;

        internal Vector PhotoPreviewTranslationForTest =>
            new(PhotoPreviewTranslate.X, PhotoPreviewTranslate.Y);

        internal void ZoomPhotoPreviewForTest(int delta, double x, double y) =>
            ApplyPhotoPreviewZoom(delta, new Point(x, y));

        internal void PanPhotoPreviewForTest(double x, double y) =>
            PanPhotoPreviewTo(x, y);


    }
}
