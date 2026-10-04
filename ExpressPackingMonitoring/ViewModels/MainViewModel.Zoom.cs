using System.Diagnostics;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 「面单放大」这一步：按主画面上的放大取景框把这一帧裁成特写。
    ///
    /// 相位机（放大 → 停留 → 还原）、平移/缩放缓动、以及放大时画中画的淡出都在这里，
    /// 和采集/预览主循环分开：主循环只问"这一帧该用哪份画面"，不用管动画细节。
    /// 放大位置只认放大取景框，识别结果与识别来源都不参与。
    /// </summary>
    public partial class MainViewModel
    {
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
                if (LastZoomRect != System.Windows.Rect.Empty) LastZoomRect = System.Windows.Rect.Empty;
                if (_zoomPhase != ZoomPhase.None)
                {
                    // 解锁识别框调整取景范围时中途停掉放大，预览回到整帧后拖动才准
                    _zoomPhase = ZoomPhase.None;
                    IsZoomingActive = false;
                }
                if (_isScanning)
                {
                    _isScanning = false;
                    Debug.WriteLine($"[Zoom] 扫码已触发但未执行缩放: ZoomEnabled={Config.EnableSmartZoom}, GuideLocked={IsCameraBarcodeGuideLocked}");
                }

                _overlayZoomFadePercent = 100;
                return currentFrame;
            }

            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.Zoom, frameSequence);
            // 放大位置只认主画面上的放大取景框：识别结果不参与，
            // 所以识别来源选副画面时也不会把副摄坐标系里的数值套到主画面上。
            System.Windows.Rect zoomBox = CameraBarcodeGuideLayout.ToDisplayRect(
                ZoomGuideGeometry,
                new System.Windows.Rect(0, 0, currentFrame.Width, currentFrame.Height));
            // 倍率也只有这一个来源：框住多大就放大铺满多大，没有第二个倍率参数。
            double zoomScale = ZoomCropPolicy.ResolveScale(
                currentFrame.Width,
                currentFrame.Height,
                zoomBox);
            if (_zoomPhase == ZoomPhase.ZoomingIn)
            {
                RuntimeLog.Info(
                    "Zoom",
                    $"Applying zoom-box centered zoom scale={zoomScale:F2}, box=({zoomBox.X:F1},{zoomBox.Y:F1},{zoomBox.Width:F1},{zoomBox.Height:F1})");
            }

            var currentZoomRect = ToCvRect(ZoomCropPolicy.CreateCropRect(
                    currentFrame.Width,
                    currentFrame.Height,
                    zoomScale,
                    zoomBox))
                .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));

            if (currentZoomRect.Width > 0 && currentZoomRect.Height > 0 && _zoomPhase == ZoomPhase.None)
            {
                LastZoomRect = new System.Windows.Rect(currentZoomRect.X, currentZoomRect.Y, currentZoomRect.Width, currentZoomRect.Height);
            }

            Mat processedFrame = currentFrame;
            if (!_isScanning)
            {
                _overlayZoomFadePercent = (int)Math.Round(overlayFadePercent);
                return processedFrame;
            }

            if (_delayBeforeZooming && (DateTime.Now - _lastScanTime).TotalMilliseconds >= Config.ZoomDelaySeconds * 1000.0)
            {
                _delayBeforeZooming = false;
                _zoomPhase = ZoomPhase.ZoomingIn;
                _zoomPhaseStartTime = DateTime.Now;
                LastZoomRect = System.Windows.Rect.Empty;
                IsZoomingActive = true;
                Debug.WriteLine($"[Zoom] 缩放触发: Delay={Config.ZoomDelaySeconds}s, Scale={zoomScale:F2}");
            }

            // 根据缩放阶段计算动画倍率
            double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;
            double animatedScale = 1.0;
            // 平移进度（0 = 整帧中心，1 = 已滑到放大取景框中心）。
            // 与倍率共用同一条缓动曲线，缩放和平移同时起步、同时停稳，
            // 不会再出现"倍率在缓动、画面却一步跳到位"的割裂感。
            double panProgress = 0.0;
            bool applyZoom = false;

            if (_zoomPhase == ZoomPhase.ZoomingIn)
            {
                double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                double eased = SmoothStep(t);
                animatedScale = 1.0 + (zoomScale - 1.0) * eased;
                panProgress = eased;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 100.0 * (1.0 - eased);
                applyZoom = true;
                if (t >= 1.0)
                {
                    _zoomPhase = ZoomPhase.Holding;
                    _zoomPhaseStartTime = DateTime.Now;
                }
            }
            else if (_zoomPhase == ZoomPhase.Holding)
            {
                animatedScale = zoomScale;
                panProgress = 1.0;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 0.0;
                applyZoom = true;
                if ((DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds >= Config.ZoomDurationSeconds * 1000.0)
                {
                    _zoomPhase = ZoomPhase.ZoomingOut;
                    _zoomPhaseStartTime = DateTime.Now;
                }
            }
            else if (_zoomPhase == ZoomPhase.ZoomingOut)
            {
                double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                double eased = SmoothStep(t);
                animatedScale = zoomScale - (zoomScale - 1.0) * eased;
                // 还原时反着走：从框中心滑回整帧中心
                panProgress = 1.0 - eased;
                if (Config.HideOverlayDuringZoom)
                    overlayFadePercent = 100.0 * eased;
                applyZoom = true;
                if (t >= 1.0)
                {
                    _zoomPhase = ZoomPhase.None;
                    _isScanning = false;
                    IsZoomingActive = false;
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
                        Cv2.Resize(zoomed, processedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                        zoomed.Dispose();
                    }
                }
            }

            _overlayZoomFadePercent = (int)Math.Round(overlayFadePercent);
            return processedFrame;
        }
    }
}
