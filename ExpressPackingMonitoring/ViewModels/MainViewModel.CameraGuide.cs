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
                IsCameraBarcodeGuideLocked);

        /// <summary>
        /// 放大取景框几何：主画面坐标，与识别来源无关。识别结果不再参与放大位置判定。
        /// </summary>
        public CameraBarcodeGuideGeometry ZoomGuideGeometry =>
            new(
                Config?.ZoomGuideWidthRatio ?? AppConfig.DefaultZoomGuideRatio,
                Config?.ZoomGuideHeightRatio ?? AppConfig.DefaultZoomGuideRatio,
                Config?.ZoomGuideOffsetX ?? 0,
                Config?.ZoomGuideOffsetY ?? 0);

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
            && (IsEditingOverlayPreview
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
            IsEditingOverlayPreview
                ? CurrentOverlayGuideGeometry
                : new CameraBarcodeGuideGeometry(
                    Config?.CameraBarcodeGuideWidthRatio ?? CameraBarcodeGuideGeometry.Default.WidthRatio,
                    Config?.CameraBarcodeGuideHeightRatio ?? CameraBarcodeGuideGeometry.Default.HeightRatio,
                    Config?.CameraBarcodeGuideOffsetX ?? CameraBarcodeGuideGeometry.Default.OffsetX,
                    Config?.CameraBarcodeGuideOffsetY ?? CameraBarcodeGuideGeometry.Default.OffsetY);

        /// <summary>
        /// 主界面拖动识别框后写回配置。拖动过程即时生效但不落盘，松手时才保存，
        /// 避免鼠标每移动一次就写一遍配置文件。
        /// </summary>
        public void ApplyCameraBarcodeGuideGeometry(CameraBarcodeGuideGeometry geometry, bool persist)
        {
            // 编辑叠加画面时写回那一路的一组，其余情况写回主摄的。
            if (IsEditingOverlayPreview && _editingOverlayChannelNumber > 0)
            {
                ApplyOverlayBarcodeGuideGeometry(_editingOverlayChannelNumber, geometry, persist);
                return;
            }

            if (Config == null)
                return;

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

        /// <summary>识别框锁定开关：解锁后才能在主界面拖动调整</summary>
        public void ToggleCameraBarcodeGuideLock() => IsCameraBarcodeGuideLocked = !IsCameraBarcodeGuideLocked;
    }
}
