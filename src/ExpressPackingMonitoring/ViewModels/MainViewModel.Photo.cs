using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UpdateCore;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    private PhotoStore? _photoStore;
    private PhotoCaptureService? _photoCaptureService;
    private int _photoCaptureInProgress;
    private readonly object _photoFrameRequestSync = new();
    private readonly SemaphoreSlim _photoCaptureGate = new(1, 1);
    private TaskCompletionSource<Mat?>? _pendingPhotoFrame;

    public bool IsPhotoCaptureButtonVisible =>
        Capabilities.CanOperatePhoto
        && (Config.ShowPhotoCaptureButton || !Capabilities.CanOperateVideo);

    internal PhotoStore PhotoStore =>
        _photoStore ??= new PhotoStore(_dbFilePath);

    private PhotoCaptureService PhotoCaptureService =>
        _photoCaptureService ??= new PhotoCaptureService(
            PhotoStore,
            ResolvePhotoRoot,
            ResolvePhotoArchiveRoot);

    internal void SetPhotoCaptureButtonVisibility(bool visible)
    {
        Config.ShowPhotoCaptureButton = visible;
        OnPropertyChanged(nameof(IsPhotoCaptureButtonVisible));
    }

    internal void TogglePhotoCaptureButtonVisibility()
    {
        if (!Capabilities.CanOperatePhoto)
        {
            ShowToast(
                AppLanguage.Get("当前用途不支持拍照"),
                ToastSeverity.Information);
            return;
        }

        bool original = Config.ShowPhotoCaptureButton;
        SetPhotoCaptureButtonVisibility(!original);
        if (!SaveConfig(notifyUser: true))
        {
            SetPhotoCaptureButtonVisibility(original);
            return;
        }

        ShowToast(
            AppLanguage.Get(
                Config.ShowPhotoCaptureButton
                    ? "拍照按钮已显示"
                    : "拍照按钮已隐藏"),
            ToastSeverity.Information);
    }

    internal async Task CapturePhotoAsync(System.Windows.Window? owner = null)
    {
        RuntimeLog.Info(
            "Photo",
            $"Capture requested capability={Capabilities.CanOperatePhoto}, "
            + $"purposeSwitchPending={_purposeSwitchPending}, "
            + $"captureInProgress={PhotoCaptureInProgress}");
        if (!Capabilities.CanOperatePhoto)
        {
            RuntimeLog.Warn("Photo", "Capture ignored because the capability is disabled");
            return;
        }
        if (_purposeSwitchPending)
        {
            ShowToast(
                AppLanguage.Get("正在切换用途，暂时不能拍照"),
                ToastSeverity.Information);
            return;
        }
        if (!await _photoCaptureGate.WaitAsync(0))
        {
            RuntimeLog.Warn("Photo", "Capture ignored because another photo is still being saved");
            ShowToast(
                AppLanguage.Get("正在保存上一张照片"),
                ToastSeverity.Information);
            return;
        }

        string trackingNumber = JdBarcodePolicy.Waybill(
            string.IsNullOrWhiteSpace(CurrentOrderId)
                ? ScanInputText
                : CurrentOrderId);
        Interlocked.Increment(ref _photoCaptureInProgress);
        try
        {
            Mat? frame = await RequestPhotoFrameAsync();
            if (frame == null)
            {
                RuntimeLog.Warn("Photo", "Capture aborted because no camera frame was available");
                ShowToast(
                    AppLanguage.Get("摄像头画面尚未就绪"),
                    ToastSeverity.Warning);
                return;
            }
            PhotoRecord photo = await PhotoCaptureService.CaptureAsync(
                frame,
                trackingNumber,
                CurrentMode,
                Config.NodeId,
                Config.NodeName);
            RuntimeLog.Info(
                "Photo",
                $"Capture completed id={photo.Id}, bytes={photo.FileSizeBytes}");
            ShowToast(
                string.IsNullOrWhiteSpace(trackingNumber)
                    ? AppLanguage.Get("照片已保存")
                    : AppLanguage.Format("照片已保存：{0}", trackingNumber),
                ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Photo", "Capture failed", ex);
            AppDialog.Error(
                owner,
                AppLanguage.Format("拍照失败：{0}", ex.Message),
                AppLanguage.Get("照片取证"));
        }
        finally
        {
            Interlocked.Decrement(ref _photoCaptureInProgress);
            _photoCaptureGate.Release();
        }
    }

    internal async Task DeletePhotoAsync(long photoId)
    {
        await PhotoCaptureService.DeleteLocalPhotoAsync(photoId);
    }

    internal async Task RetryPhotoArchiveAsync(long photoId)
    {
        PhotoCaptureService service = PhotoCaptureService;
        await Task.Run(() => service.RetryArchiveAsync(photoId));
    }

    internal bool PhotoCaptureInProgress =>
        Volatile.Read(ref _photoCaptureInProgress) != 0;

    private async Task<Mat?> RequestPhotoFrameAsync()
    {
        var completion = new TaskCompletionSource<Mat?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_photoFrameRequestSync)
        {
            _pendingPhotoFrame?.TrySetResult(null);
            _pendingPhotoFrame = completion;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using CancellationTokenRegistration registration =
            timeout.Token.Register(() =>
            {
                lock (_photoFrameRequestSync)
                {
                    if (ReferenceEquals(_pendingPhotoFrame, completion))
                        _pendingPhotoFrame = null;
                }
                completion.TrySetResult(null);
            });
        return await completion.Task;
    }

    private void TryFulfillPendingPhotoFrame(Mat frame)
    {
        TaskCompletionSource<Mat?>? completion;
        lock (_photoFrameRequestSync)
        {
            completion = _pendingPhotoFrame;
            _pendingPhotoFrame = null;
        }
        if (completion == null || frame.Empty())
            return;
        TryCompletePhotoFrameRequest(completion, frame);
    }

    internal static bool TryCompletePhotoFrameRequest(
        TaskCompletionSource<Mat?>? completion,
        Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (completion == null || completion.Task.IsCompleted || frame.Empty())
            return false;

        Mat clone = frame.Clone();
        if (completion.TrySetResult(clone))
            return true;

        clone.Dispose();
        return false;
    }

    private static string ResolvePhotoRoot()
    {
        string systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            ?? "C:\\";
        return Path.Combine(
            systemDrive,
            BrandMetadata.ArtifactPrefix + "Photos");
    }

    private string? ResolvePhotoArchiveRoot()
    {
        try
        {
            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                Config,
                allowDefaultFallback: false);
            return plan.RequiresNetworkArchive ? plan.ArchiveTarget : null;
        }
        catch
        {
            return null;
        }
    }
}
