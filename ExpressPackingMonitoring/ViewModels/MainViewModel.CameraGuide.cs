using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>主界面上识别框是否锁住；锁住时不能拖动，避免误改取景范围</summary>
        public bool IsCameraBarcodeGuideLocked
        {
            get => Config == null || Config.CameraBarcodeGuideLocked;
            set
            {
                if (Config == null || Config.CameraBarcodeGuideLocked == value)
                    return;

                Config.CameraBarcodeGuideLocked = value;
                SaveConfig();
                RuntimeLog.Info("CameraBarcode", $"Guide locked={value}");
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
                OnPropertyChanged(nameof(CameraBarcodeGuideLockTipText));
            }
        }

        /// <summary>
        /// 锁图标提示。这里用属性而不是样式触发器：自动本地化会在加载时写一次 ToolTip，
        /// 触发器再改文字会被压在下面，状态切换后提示就不再跟着变了。
        /// </summary>
        public string CameraBarcodeGuideLockTipText => IsCameraBarcodeGuideLocked
            ? AppLanguage.CameraBarcodeGuideLockedTipText
            : AppLanguage.CameraBarcodeGuideUnlockedTipText;

        /// <summary>
        /// 扫码放大当前是否可用。解锁识别框调整取景范围时不放大：预览被裁切后框对不准，
        /// 拖动也会摆错；锁回识别框后立即恢复。
        /// </summary>
        private bool CanApplyZoom =>
            ZoomCropPolicy.ShouldApplyZoom(
                Config?.EnableSmartZoom == true,
                IsCameraBarcodeGuideLocked,
                IsEditingZoomGuide);

        /// <summary>
        /// 放大取景框几何：主画面坐标，与识别来源无关。识别结果不再参与放大位置判定。
        /// 返回的已是"按当前旋转换算过"的帧坐标，并且规范成和画面同长宽比的那一块：
        /// 框住的地方就是放大后填满画面的地方，绘制与裁剪都直接吃这一份。
        /// </summary>
        public CameraBarcodeGuideGeometry ZoomGuideGeometry =>
            CameraBarcodeGuideLayout.NormalizeToFrameAspect(
                CameraBarcodeGuideLayout.Rotate(
                    new CameraBarcodeGuideGeometry(
                        Config?.ZoomGuideWidthRatio ?? AppConfig.DefaultZoomGuideRatio,
                        Config?.ZoomGuideHeightRatio ?? AppConfig.DefaultZoomGuideRatio,
                        Config?.ZoomGuideOffsetX ?? 0,
                        Config?.ZoomGuideOffsetY ?? 0),
                    MainCameraRotationDegrees));

        /// <summary>主摄当前旋转角度；配置还没加载或值是哨兵时按不旋转处理。</summary>
        private int MainCameraRotationDegrees => Config?.CameraRotationDegrees ?? 0;

        /// <summary>
        /// 主摄识别框几何，已换算到"旋转后的帧坐标"：绘制与识别都吃这一份。
        /// 配置里存的是原生坐标，改旋转后框仍盖住同一块画面区域。
        /// </summary>
        internal CameraBarcodeGuideGeometry MainCameraBarcodeGuideGeometry =>
            CameraBarcodeGuideLayout.Rotate(
                new CameraBarcodeGuideGeometry(
                    Config?.CameraBarcodeGuideWidthRatio ?? CameraBarcodeGuideGeometry.Default.WidthRatio,
                    Config?.CameraBarcodeGuideHeightRatio ?? CameraBarcodeGuideGeometry.Default.HeightRatio,
                    Config?.CameraBarcodeGuideOffsetX ?? CameraBarcodeGuideGeometry.Default.OffsetX,
                    Config?.CameraBarcodeGuideOffsetY ?? CameraBarcodeGuideGeometry.Default.OffsetY),
                MainCameraRotationDegrees);

        /// <summary>
        /// 是否正在主画面上调整放大取景框（从设置页"调整放大位置"进入）。
        /// 这一屏里拖动框写的是放大取景框，与识别框、识别来源都无关。
        /// </summary>
        public bool IsEditingZoomGuide { get; private set; }

        /// <summary>
        /// 副摄取景编辑或放大取景框编辑：两种都是"整屏拖一个框"的预览编辑态，
        /// 界面据此显示取景框、"完成"按钮与对应提示。
        /// </summary>
        public bool IsPreviewGuideEditing => IsEditingOverlayPreview || IsEditingZoomGuide;

        /// <summary>进入放大取景框编辑；已在编辑态时不重复进入。</summary>
        internal void EnterZoomGuideEdit()
        {
            if (IsEditingZoomGuide || IsEditingOverlayPreview)
                return;

            IsEditingZoomGuide = true;
            RuntimeLog.Info("ZoomGuide", "进入放大取景框编辑");
            NotifyZoomGuideEditStateChanged();
            // 进入编辑就把正在跑的放大停掉：预览被裁切时框摆不准，也没法无遮挡框选。
            StopZoomForGuideEditing();
        }

        /// <summary>退出放大取景框编辑；没在编辑时不做事。</summary>
        internal void ExitZoomGuideEdit()
        {
            if (!IsEditingZoomGuide)
                return;

            IsEditingZoomGuide = false;
            RuntimeLog.Info("ZoomGuide", "退出放大取景框编辑");
            NotifyZoomGuideEditStateChanged();
        }

        private void NotifyZoomGuideEditStateChanged()
        {
            OnPropertyChanged(nameof(IsEditingZoomGuide));
            OnPropertyChanged(nameof(IsPreviewGuideEditing));
            OnPropertyChanged(nameof(CurrentCameraBarcodeGuideGeometry));
            OnPropertyChanged(nameof(CameraBarcodeStatusText));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
            OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
        }

        /// <summary>把放大阶段复位成"没在放大"，预览立刻回到整帧。</summary>
        private void StopZoomForGuideEditing()
        {
            _isScanning = false;
            _delayBeforeZooming = false;
            _zoomPhase = ZoomPhase.None;
            LastZoomRect = System.Windows.Rect.Empty;
            IsZoomingActive = false;
        }

        /// <summary>
        /// 识别框当前能否拖动。摄像头休眠时识别框本来就不显示，扫码放大期间
        /// 预览画面已被裁切、和取景用的整帧比例对不上，这两种状态下不接受拖动。
        ///
        /// 进入某一幅副画面的取景编辑屏时一定可以拖：这一屏就是用来调裁剪的，
        /// 不该再受主界面那个小锁、或"摄像头自动识别面单"开关的影响。
        /// </summary>
        public bool IsCameraBarcodeGuideEditable =>
            !IsCameraSleeping
            && !IsZoomingActive
            && (IsEditingZoomGuide
                || IsEditingOverlayPreview
                // 主界面：识别开着、没锁、而且识别输入不是叠加画面（否则那一路的取景在编辑屏里改）
                || (Config?.EnableCameraBarcodeRecognition == true
                    && !IsCameraBarcodeGuideLocked
                    && !ShouldUseOverlayChannelForBarcode));

        /// <summary>当前生效的识别框几何</summary>
        /// <remarks>
        /// 正在编辑某一路叠加画面的取景时，这里返回的是那一路的一组几何 —— 界面因此完全不用区分为哪一路，
        /// 走的就是主摄那套已经验证过的摆放与拖动逻辑，只是数据换成了那一路的。
        /// </remarks>
        public CameraBarcodeGuideGeometry CurrentCameraBarcodeGuideGeometry =>
            IsEditingZoomGuide
                ? ZoomGuideGeometry
                : IsEditingOverlayPreview
                    ? CurrentOverlayGuideGeometry
                    : MainCameraBarcodeGuideGeometry;

        /// <summary>
        /// 主界面拖动识别框后写回配置。拖动过程即时生效但不落盘，松手时才保存，
        /// 避免鼠标每移动一次就写一遍配置文件。
        /// </summary>
        public void ApplyCameraBarcodeGuideGeometry(CameraBarcodeGuideGeometry geometry, bool persist)
        {
            // 传进来的是"旋转后的帧坐标"（拖动换算出来的），落盘前一律还原成原生画面坐标，
            // 否则改一次旋转就等于把框又转了一次。

            // 放大取景框编辑屏：写回放大取景框那一组，与识别框完全分开。
            if (IsEditingZoomGuide)
            {
                ApplyZoomGuideGeometry(
                    CameraBarcodeGuideLayout.RotateInverse(geometry, MainCameraRotationDegrees),
                    persist);
                return;
            }

            // 编辑叠加画面时写回那一路的一组，其余情况写回主摄的。
            if (IsEditingOverlayPreview && _editingOverlayChannelNumber > 0)
            {
                int overlayRotation = FindOverlayChannel(_editingOverlayChannelNumber)?.Config.RotationDegrees ?? 0;
                ApplyOverlayBarcodeGuideGeometry(
                    _editingOverlayChannelNumber,
                    CameraBarcodeGuideLayout.RotateInverse(geometry, overlayRotation),
                    persist);
                return;
            }

            if (Config == null)
                return;

            geometry = CameraBarcodeGuideLayout.RotateInverse(geometry, MainCameraRotationDegrees);
            Config.CameraBarcodeGuideWidthRatio = Math.Clamp(
                geometry.WidthRatio,
                CameraBarcodeGuideLayout.MinRatio,
                CameraBarcodeGuideLayout.MaxRatio);
            Config.CameraBarcodeGuideHeightRatio = Math.Clamp(
                geometry.HeightRatio,
                CameraBarcodeGuideLayout.MinRatio,
                CameraBarcodeGuideLayout.MaxRatio);
            Config.CameraBarcodeGuideOffsetX = Math.Clamp(geometry.OffsetX, -1.0, 1.0);
            Config.CameraBarcodeGuideOffsetY = Math.Clamp(geometry.OffsetY, -1.0, 1.0);

            if (!persist)
                return;

            SaveConfig(notifyUser: true);
            RuntimeLog.Info(
                "CameraBarcode",
                $"Guide adjusted width={Config.CameraBarcodeGuideWidthRatio:F3} height={Config.CameraBarcodeGuideHeightRatio:F3} offsetX={Config.CameraBarcodeGuideOffsetX:F3} offsetY={Config.CameraBarcodeGuideOffsetY:F3}");
        }

        /// <summary>
        /// 放大取景框编辑屏拖动后写回配置：与识别框同一套换算与夹紧范围
        /// （比例 30%~100%，偏移 ±1），拖动过程只改内存，松手才落盘。
        /// </summary>
        private void ApplyZoomGuideGeometry(CameraBarcodeGuideGeometry geometry, bool persist)
        {
            if (Config == null)
                return;

            Config.ZoomGuideWidthRatio = Math.Clamp(
                geometry.WidthRatio,
                CameraBarcodeGuideLayout.MinRatio,
                CameraBarcodeGuideLayout.MaxRatio);
            Config.ZoomGuideHeightRatio = Math.Clamp(
                geometry.HeightRatio,
                CameraBarcodeGuideLayout.MinRatio,
                CameraBarcodeGuideLayout.MaxRatio);
            Config.ZoomGuideOffsetX = Math.Clamp(geometry.OffsetX, -1.0, 1.0);
            Config.ZoomGuideOffsetY = Math.Clamp(geometry.OffsetY, -1.0, 1.0);

            // 落盘前收成和画面同长宽比的那一块：下次读出来、拖动把手和实际裁剪都是同一块区域。
            CameraBarcodeGuideGeometry normalized =
                CameraBarcodeGuideLayout.NormalizeToFrameAspect(new CameraBarcodeGuideGeometry(
                    Config.ZoomGuideWidthRatio,
                    Config.ZoomGuideHeightRatio,
                    Config.ZoomGuideOffsetX,
                    Config.ZoomGuideOffsetY));
            Config.ZoomGuideWidthRatio = normalized.WidthRatio;
            Config.ZoomGuideHeightRatio = normalized.HeightRatio;
            Config.ZoomGuideOffsetX = normalized.OffsetX;
            Config.ZoomGuideOffsetY = normalized.OffsetY;

            if (!persist)
                return;

            SaveConfig(notifyUser: true);
            RuntimeLog.Info(
                "ZoomGuide",
                $"Zoom guide adjusted width={Config.ZoomGuideWidthRatio:F3} height={Config.ZoomGuideHeightRatio:F3} offsetX={Config.ZoomGuideOffsetX:F3} offsetY={Config.ZoomGuideOffsetY:F3}");
        }

        /// <summary>识别框锁定开关：解锁后才能在主界面拖动调整</summary>
        public void ToggleCameraBarcodeGuideLock() => IsCameraBarcodeGuideLocked = !IsCameraBarcodeGuideLocked;
    }
}
