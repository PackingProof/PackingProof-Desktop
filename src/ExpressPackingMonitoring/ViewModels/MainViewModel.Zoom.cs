using System.Diagnostics;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 「面单放大」这一步：按主画面上的放大取景框把这一帧裁成特写。
    ///
    /// 相位机（等待 → 放大 → 停留 → 还原）、平移/缩放缓动、以及放大时画中画的淡出都在这里，
    /// 和采集/预览主循环分开：主循环只问"这一帧该用哪份画面"，不用管动画细节。
    /// 放大状态只有一个来源 —— 相位加相位开始时刻，别的字段都别记"放大到哪一步了"。
    /// 智能跟随时逐帧跟随当前选中的单个 OBB 面单；否则使用固定放大取景框。
    /// </summary>
    public partial class MainViewModel
    {
        private VisionObbDetection? _smartZoomObbTarget;

        /// <summary>
        /// 扫码或手动开录后起一次放大。面单放大没开、或正在"调放大位置"那一屏
        /// （那一屏要拖动放大取景框）时不放大；"放大前等待"到点在处理循环里再步进。
        /// </summary>
        private void StartZoomForScan()
        {
            if (!CanApplyZoom)
                return;

            _smartZoomObbTarget = ResolveSmartZoomTarget();
            SetZoomPhase(Config.ZoomDelaySeconds > 0 ? ZoomPhase.Waiting : ZoomPhase.ZoomingIn);
            Debug.WriteLine($"[Zoom] 触发放大: ID={CurrentOrderId}, Delay={Config.ZoomDelaySeconds}s");
        }

        private VisionObbDetection? ResolveSmartZoomTarget()
        {
            if (!Config.EnableVisionObbSmartZoom
                || CameraVisionSmartZoomTarget is not { } detection)
            {
                return null;
            }

            return detection;
        }

        /// <summary>
        /// 把放大收干净：相位复位，预览回到整帧，识别框与放大取景框重新显示。
        /// 停录与进"调放大位置"都会中途打断放大，两条路径都走这里 ——
        /// 只复位相位而不发通知，界面那两层框会被残留状态一直藏住。
        /// </summary>
        private void ResetZoomState()
        {
            SetZoomPhase(ZoomPhase.None);
            _smartZoomObbTarget = null;
            _overlayZoomFadePercent = 100;
        }

        /// <summary>
        /// 步进放大相位，并在"是否正在放大"翻转时通知界面
        /// （识别框与放大取景框都按它隐藏/恢复，拖动识别框也要据此放开/关掉）。
        /// </summary>
        private void SetZoomPhase(ZoomPhase phase)
        {
            bool wasZooming = IsZoomingActive;
            _zoomPhase = phase;
            _zoomPhaseStartTime = DateTime.Now;
            if (wasZooming == IsZoomingActive)
                return;

            OnPropertyChanged(nameof(IsZoomingActive));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
        }

        /// <summary>
        /// 按放大取景框处理这一帧：可放大时返回裁好的特写帧（含平移与缩放动画），
        /// 否则复位放大相位并原样返回整帧。
        ///
        /// 顺带更新 <see cref="_overlayZoomFadePercent"/>：画中画这一帧该用的淡出系数。
        /// 写进字段而不是只当返回值，是因为预录回灌那条线程也读它，
        /// 预录段才能和实时画面保持同一个淡出程度。
        /// </summary>
        private Mat ApplyZoomToFrame(Mat currentFrame, long frameSequence)
        {
            // 放大特写时"周围的画中画淡出"的系数（0~100，100 = 完全不淡出）。
            // 与缩放共用同一条缓动曲线和同一个 animDuration（= 设置页的"过渡时长"）：
            // 小窗跟着特写一起淡出/淡入、同时停稳，不再多一个时长参数要维护；
            // 关掉"平滑过渡"时它也跟着变成瞬切，两边的节奏永远一致。
            double overlayFadePercent = 100.0;

            if (!CanApplyZoom)
            {
                // 面单放大关了、或进了"调放大位置"那一屏：把放大收干净，预览回到整帧。
                ResetZoomState();
                return currentFrame;
            }

            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.Zoom, frameSequence);

            if (_zoomPhase == ZoomPhase.Waiting)
            {
                // "放大前等待"还没到点：预览保持整帧。
                if ((DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds < Config.ZoomDelaySeconds * 1000.0)
                {
                    _overlayZoomFadePercent = 100;
                    return currentFrame;
                }

                SetZoomPhase(ZoomPhase.ZoomingIn);
            }

            if (_zoomPhase == ZoomPhase.None)
            {
                _overlayZoomFadePercent = 100;
                return currentFrame;
            }

            System.Windows.Rect zoomBox = _smartZoomObbTarget is { } smartTarget
                ? VisionSmartZoomPolicy.ResolveTargetRect(
                    smartTarget,
                    currentFrame.Width,
                    currentFrame.Height)
                : System.Windows.Rect.Empty;
            bool usingSmartTarget = !zoomBox.IsEmpty;
            if (!usingSmartTarget)
            {
                // 没有可用的本次识别坐标时，继续使用固定放大取景框。
                zoomBox = CameraBarcodeGuideLayout.ToDisplayRect(
                    ZoomGuideGeometry,
                    new System.Windows.Rect(0, 0, currentFrame.Width, currentFrame.Height));
            }
            // 倍率由目标框决定：智能模式是本次选中的单个 OBB，否则是固定取景框。
            double zoomScale = ZoomCropPolicy.ResolveScale(
                currentFrame.Width,
                currentFrame.Height,
                zoomBox);
            if (_zoomPhase == ZoomPhase.ZoomingIn)
            {
                RuntimeLog.Info(
                    "Zoom",
                    $"Applying {(usingSmartTarget ? "OBB" : "guide")} zoom scale={zoomScale:F2}, "
                        + $"box=({zoomBox.X:F1},{zoomBox.Y:F1},{zoomBox.Width:F1},{zoomBox.Height:F1})");
            }

            Mat processedFrame = currentFrame;

            // 触发时已经锁存目标；整个放大过程只使用这份坐标，不再跟随后续识别结果。
            double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;
            double animatedScale = 1.0;
            double panProgress = 0.0;
            bool applyZoom = false;

            if (_zoomPhase == ZoomPhase.ZoomingIn)
            {
                double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                double eased = SmoothStep(t);
                animatedScale = 1.0 + ((zoomScale - 1.0) * eased);
                panProgress = eased;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 100.0 * (1.0 - eased);
                applyZoom = true;
                if (t >= 1.0)
                    SetZoomPhase(ZoomPhase.Holding);
            }
            else if (_zoomPhase == ZoomPhase.Holding)
            {
                animatedScale = zoomScale;
                panProgress = 1.0;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 0.0;
                applyZoom = true;
                if ((DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds
                    >= Config.ZoomDurationSeconds * 1000.0)
                {
                    SetZoomPhase(ZoomPhase.ZoomingOut);
                }
            }
            else if (_zoomPhase == ZoomPhase.ZoomingOut)
            {
                double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                double eased = SmoothStep(t);
                animatedScale = zoomScale - ((zoomScale - 1.0) * eased);
                panProgress = 1.0 - eased;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 100.0 * eased;
                applyZoom = true;
                if (t >= 1.0)
                {
                    SetZoomPhase(ZoomPhase.None);
                    _smartZoomObbTarget = null;
                    Debug.WriteLine("[Zoom] 缩放动画结束，恢复原样");
                }
            }

            if (applyZoom && animatedScale > 1.001)
            {
                int animW = (int)(currentFrame.Width / animatedScale);
                int animH = (int)(currentFrame.Height / animatedScale);
                if (animW > 0 && animH > 0 && animW <= currentFrame.Width && animH <= currentFrame.Height)
                {
                    System.Windows.Point panCenter = ZoomCropPolicy.ResolvePanCenter(
                        currentFrame.Width,
                        currentFrame.Height,
                        zoomBox,
                        panProgress);
                    var animRect = ToCvRect(ZoomCropPolicy.CreateCropRect(
                            currentFrame.Width,
                            currentFrame.Height,
                            animatedScale,
                            panCenter.X,
                            panCenter.Y))
                        .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));
                    if (animRect.Width > 0 && animRect.Height > 0)
                    {
                        var zoomed = currentFrame.Clone(animRect);
                        processedFrame = new Mat();
                        // 还原尺寸必须跟当前帧一致：摄像头实际协商到的分辨率未必等于配置里的分辨率，
                        // 按配置尺寸还原会让放大这几秒的帧跟其它帧尺寸不同，录像按固定尺寸喂进去就错位。
                        Cv2.Resize(zoomed, processedFrame, new OpenCvSharp.Size(currentFrame.Width, currentFrame.Height));
                        zoomed.Dispose();
                    }
                }
            }

            _overlayZoomFadePercent = (int)Math.Round(overlayFadePercent);
            return processedFrame;
        }
    }
}
