using System;
using System.Globalization;
using System.IO;
using System.Windows;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    public class VideoItem
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Duration { get; set; } = "";
        public double DurationSeconds { get; set; }
        public string FileSize { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public string StopReason { get; set; } = "";
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public string SourceDisplay { get; set; } = "";
        public string SourceDeviceId { get; set; } = "";
        public bool IsStoredOnHost { get; set; }
        public bool IsMissing { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsArchiveWarning { get; set; }
        public bool IsPhoto { get; set; }
        public long PhotoId { get; set; }
        public string PhotoArchiveStatus { get; set; } = "";
        public bool IsPhotoArchiveCompleted { get; set; }
        public string PhotoDimensions { get; set; } = "";
        public string ArchiveStatusText { get; set; } = "";
        public string DeleteReason { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
        public FileInfo? File { get; set; }

        /// <summary>数据库记录 ID：右键删除要知道删的是哪一条。</summary>
        public long RecordId { get; set; }

        // 悬浮提示与右键菜单需要的明细，与 Web 端 buildVideoTooltip 的字段保持一致。
        public string TrackingNumber { get; set; } = "";
        public string SourceOrderId { get; set; } = "";
        public string BuyerMessage { get; set; } = "";
        public string SellerMemo { get; set; } = "";
        public string ProductInfo { get; set; } = "";
        public string OrderInfoPushTime { get; set; } = "";
        public DateTime StartTime { get; set; }
        public string FileName { get; set; } = "";

        /// <summary>右键复制单号时优先取快递单号，与列表显示口径一致。</summary>
        public string CopyableOrderId =>
            !string.IsNullOrWhiteSpace(TrackingNumber) ? TrackingNumber
            : !string.IsNullOrWhiteSpace(OrderId) ? OrderId
            : "";

        /// <summary>只有本地真实存在的文件才能在资源管理器里定位。</summary>
        public bool CanLocateFile =>
            !string.IsNullOrWhiteSpace(FullPath) && !IsDeleted && !IsStoredOnHost && !IsMissing;

        /// <summary>能不能删掉这条录像（右键“删除录像”，服务端还会在锁内复查一次）。</summary>
        public bool CanDelete { get; set; }
        public string DeleteMenuText => IsPhoto
            ? AppLanguage.Get("删除照片…")
            : AppLanguage.Get("删除录像…");

        /// <summary>不能删除的原因，作为禁用菜单项的悬浮提示。</summary>
        public string DeleteBlockedHint { get; set; } = "";

        /// <summary>压缩副本需要原始文件、可计算时长，而且确实还有缩小空间。</summary>
        public bool CanCompress =>
            !IsPhoto
            &&
            !IsUnavailable
            && !string.IsNullOrWhiteSpace(FullPath)
            && DurationSeconds > 0
            && FileSizeBytes > VideoCompressionPolicy.Mebibyte;

        public string CompressBlockedHint
        {
            get
            {
                if (CanCompress)
                    return "";
                if (IsUnavailable || string.IsNullOrWhiteSpace(FullPath))
                    return AppLanguage.Get("录像文件不存在或当前不可访问");
                if (DurationSeconds <= 0)
                    return AppLanguage.Get("无法读取录像时长");
                if (FileSizeBytes <= VideoCompressionPolicy.Mebibyte)
                    return AppLanguage.Get("当前文件已很小，无需压缩");
                return AppLanguage.Get("当前录像无法导出");
            }
        }

        public bool CanWebClip => !IsPhoto && !IsUnavailable && RecordId > 0;

        /// <summary>这条录像在备份位置上还有归档副本：删除会保留它，确认框要说明。</summary>
        public bool HasArchiveCopy { get; set; }

        /// <summary>悬浮提示内容：短信息并排一行、长文本单独一行（Web 端同款排版）。</summary>
        public FrameworkElement ToolTipContent => PlaybackTooltipPresenter.Build(this);

        public string EncoderDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(VideoEncoder))
                    return EncodingHelper.GetEncoderLabel(VideoEncoder);
                if (!string.IsNullOrWhiteSpace(VideoCodec))
                    return EncodingHelper.GetCodecLabel(VideoCodec);
                return "";
            }
        }

        public string StatusText
        {
            get
            {
                if (IsDeleted)
                {
                    return PlaybackReasonText.CleanupStatus(DeleteReason, DeletedAt);
                }

                if (IsPhoto)
                    return PhotoArchiveStatus;

                if (IsStoredOnHost)
                    return "已备份到主机";
                if (IsArchiveWarning)
                    return ArchiveStatusText;
                return IsMissing ? "文件已丢失" : "";
            }
        }

        public bool IsUnavailable => IsDeleted || IsMissing || IsStoredOnHost;

        public string RowBadgeText => PlaybackRowBadgePolicy.GetText(StatusText, StopReason);

        public bool HasRowBadge => !string.IsNullOrWhiteSpace(RowBadgeText);

        public string SourceTagText =>
            string.IsNullOrWhiteSpace(SourceDisplay) ? "—" : SourceDisplay;

        public int SourceColorIndex
        {
            get
            {
                string key = string.IsNullOrWhiteSpace(SourceDeviceId)
                    ? SourceDisplay
                    : SourceDeviceId;
                uint hash = 2166136261;
                foreach (char value in key)
                {
                    hash ^= value;
                    hash *= 16777619;
                }
                return (int)(hash % 6) + 1;
            }
        }

        public string DateGroupKey
        {
            get
            {
                DateTime date = StartTime > DateTime.MinValue
                    ? StartTime
                    : File?.CreationTime ?? DateTime.MinValue;
                if (date == DateTime.MinValue)
                    return "—";

                CultureInfo culture = CultureInfo.CurrentUICulture;
                return string.Equals(
                    culture.TwoLetterISOLanguageName,
                    "en",
                    StringComparison.OrdinalIgnoreCase)
                    ? date.ToString("MMM d, yyyy", culture)
                    : date.ToString("yyyy年M月d日", culture);
            }
        }

        public string CaptureTimeText
        {
            get => StartTime > DateTime.MinValue
                ? StartTime.ToString("HH:mm:ss")
                : "—";
        }

        public string PrimaryMetaText
        {
            get
            {
                if (IsPhoto)
                return string.IsNullOrWhiteSpace(Duration)
                    ? FileSize
                    : $"{Duration} · {FileSize}";
                string duration = string.IsNullOrWhiteSpace(Duration) ? "—" : Duration;
                string size = string.IsNullOrWhiteSpace(FileSize) ? "—" : FileSize;
                return $"{duration} · {size}";
            }
        }

        public string RowBadgeTone
        {
            get
            {
                if (IsDeleted)
                    return "muted";
                if (IsPhoto && IsPhotoArchiveCompleted)
                    return "success";
                if (IsStoredOnHost)
                    return "success";
                if (IsMissing || IsArchiveWarning)
                    return "error";
                return "reason";
            }
        }

    }

}
