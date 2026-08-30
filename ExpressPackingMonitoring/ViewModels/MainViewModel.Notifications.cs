#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Success)
        {
            message = AppLanguage.Translate(message);
            if (_alertService != null)
            {
                _alertService.Publish(new AlertRequest
                {
                    Message = message,
                    Severity = severity,
                    Priority = AlertPriority.Normal,
                    Sound = AlertSound.None,
                    DisplayDuration = GetToastDisplayDuration(severity)
                });
                return;
            }

            PresentToast(message, GetToastDisplayDuration(severity), severity);
        }

        private static TimeSpan GetToastDisplayDuration(ToastSeverity severity) =>
            severity is ToastSeverity.Warning or ToastSeverity.Error
                ? TimeSpan.FromSeconds(4)
                : TimeSpan.FromMilliseconds(2500);

        private void PresentAlert(AlertRequest request)
        {
            if (ShouldShowPreviewAlert(request))
                PresentPreviewAlert(request);
            else
                PresentToast(request.Message, request.DisplayDuration, request.Severity);
        }

        internal static bool ShouldShowPreviewAlert(AlertRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Message))
                return false;
            if (request.Priority == AlertPriority.Critical || request.Sound is AlertSound.Warning or AlertSound.IndustrialAlarm)
                return true;

            string message = request.Message;
            string[] exceptionTerms =
            [
                "警告", "异常", "失败", "错误", "断开", "丢失", "超时", "拦截", "退款", "不一致", "无法", "过短", "太小",
                "warning", "error", "failed", "failure", "exception", "disconnected", "timeout", "invalid", "refund"
            ];
            return exceptionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        internal static string BuildPreviewOrderNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            string remarks = BuildPreviewOrderRemarkNotice(orderInfo);
            string details = BuildPreviewOrderDetailNotice(orderInfo);
            return string.Join(
                Environment.NewLine,
                new[] { remarks, details }.Where(value => value.Length > 0));
        }

        internal static string BuildPreviewOrderRemarkNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewBuyerMessage", orderInfo.BuyerMessage);
            AddPreviewOrderLine(lines, "Main.PreviewSellerMemo", orderInfo.SellerMemo);
            return string.Join(Environment.NewLine, lines);
        }

        internal static string BuildPreviewOrderDetailNotice(OrderInfo orderInfo)
        {
            if (orderInfo == null)
                return "";

            var lines = new List<string>();
            AddPreviewOrderLine(lines, "Main.PreviewProduct", orderInfo.ProductInfo);

            if (orderInfo.HasRefund || orderInfo.IsPrintedRefund)
            {
                string status = GetRefundStatusDisplayText(orderInfo);
                if (!string.Equals(status, "无退款", StringComparison.Ordinal))
                    lines.Add(AppLanguage.Format("Main.PreviewException", CompactPreviewText(status)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        internal static string BuildPreviewOrderItemCountText(OrderInfo orderInfo)
        {
            return orderInfo?.TotalItemCount > 1
                ? orderInfo.TotalItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        }

        internal static IReadOnlyList<AlertSpeechFollowup> BuildOrderInfoSpeechFollowups(
            OrderInfo orderInfo,
            bool announcementsEnabled,
            bool announceBuyerMessage,
            bool announceSellerMemo,
            bool announceProductInfo,
            bool announceTotalItemCount = true)
        {
            if (!announcementsEnabled || orderInfo == null)
                return Array.Empty<AlertSpeechFollowup>();

            var announcements = new List<AlertSpeechFollowup>();
            if (announceTotalItemCount
                && !orderInfo.HasRefund
                && !orderInfo.IsPrintedRefund
                && orderInfo.TotalItemCount > 1)
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateOrderTotalCountAnnouncement(orderInfo.TotalItemCount),
                    Sound = AlertSound.None
                });
            }
            if (announceBuyerMessage && !string.IsNullOrWhiteSpace(orderInfo.BuyerMessage))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(orderInfo.BuyerMessage),
                    Sound = AlertSound.Remark
                });
            }
            if (announceSellerMemo && !string.IsNullOrWhiteSpace(orderInfo.SellerMemo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateSellerMemoAnnouncement(orderInfo.SellerMemo),
                    Sound = AlertSound.Remark
                });
            }
            if (announceProductInfo && !string.IsNullOrWhiteSpace(orderInfo.ProductInfo))
            {
                announcements.Add(new AlertSpeechFollowup
                {
                    Text = DefaultSpeechCatalog.CreateProductAnnouncement(orderInfo.ProductInfo),
                    Sound = AlertSound.None
                });
            }
            return announcements;
        }

        private static void AddPreviewOrderLine(List<string> lines, string resourceKey, string value)
        {
            string compact = CompactPreviewText(value);
            if (compact.Length > 0)
                lines.Add(AppLanguage.Format(resourceKey, compact));
        }

        private static string CompactPreviewText(string value)
        {
            string compact = string.Join(" ", (value ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            const int maxLength = 160;
            return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
        }

        private void SetPreviewOrderNotice(OrderInfo orderInfo)
        {
            PreviewOrderRemarkText = BuildPreviewOrderRemarkNotice(orderInfo);
            PreviewOrderDetailText = BuildPreviewOrderDetailNotice(orderInfo);
            PreviewOrderItemCountText = BuildPreviewOrderItemCountText(orderInfo);
            IsPreviewOrderNoticeVisible = PreviewOrderRemarkText.Length > 0 || PreviewOrderDetailText.Length > 0;
        }

        private void ClearPreviewOrderNotice() => SetPreviewOrderNotice(null);

        private void PresentPreviewAlert(AlertRequest request)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _previewAlertCts?.Cancel();
                _previewAlertCts = new CancellationTokenSource();
                var token = _previewAlertCts.Token;
                PreviewAlertText = request.Message;
                IsPreviewAlertCritical = request.Priority == AlertPriority.Critical || request.Sound == AlertSound.IndustrialAlarm;
                IsPreviewAlertVisible = true;
                TimeSpan duration = request.DisplayDuration < TimeSpan.FromSeconds(5)
                    ? TimeSpan.FromSeconds(5)
                    : request.DisplayDuration;
                try { await Task.Delay(duration, token); }
                catch (OperationCanceledException) { return; }
                IsPreviewAlertVisible = false;
            });
        }

        private void PresentToast(
            string message,
            TimeSpan displayDuration,
            ToastSeverity severity = ToastSeverity.Success)
        {
            Application.Current?.Dispatcher?.InvokeAsync(async () =>
            {
                _toastCts?.Cancel();
                _toastCts = new CancellationTokenSource();
                var token = _toastCts.Token;
                ToastMessage = message;
                ToastSeverity = severity;
                IsToastVisible = true;
                try { await Task.Delay(displayDuration, token); }
                catch (OperationCanceledException) { return; }
                IsToastVisible = false;
            });
        }

        private void PublishScannerAlert(
            string deduplicationKey,
            string message,
            string speechText,
            int repeatCount = 1)
        {
            _alertService?.Publish(new AlertRequest
            {
                Message = message,
                SpeechText = speechText,
                Priority = AlertPriority.Normal,
                Sound = AlertSound.Warning,
                SoundRepeatCount = 1,
                SpeechRepeatCount = repeatCount,
                DisplayDuration = TimeSpan.FromMilliseconds(2500),
                DeduplicationKey = deduplicationKey,
                DeduplicationWindow = TimeSpan.FromSeconds(3)
            });
        }

    }
}
