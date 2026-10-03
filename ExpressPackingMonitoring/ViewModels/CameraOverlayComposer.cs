using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 把一路副摄像头画面缩放后贴到主画面的一角。
    ///
    /// 直接画在录像/预览共用的那一帧上（与水印同一手法），所以预览和录像天然一致：
    /// 不需要第二套发布管线，也不会出现"预览里有、录像里没有"的分叉。
    /// 条码识别在读帧阶段就已经跑完，副画面不会进入识别输入。
    /// </summary>
    internal static class CameraOverlayComposer
    {
        /// <summary>小窗边框像素宽度，让副画面从主画面里"浮"出来。</summary>
        private const int BorderThickness = 2;

        /// <summary>
        /// 贴片缓存：同一份叠加帧 + 同一个落位 + 同一种主帧通道数时，缩放、通道对齐与描边只算一次。
        ///
        /// 预录回灌会把**同一份**叠加帧连续合成上百帧（预录帧用的是回灌那一刻的叠加画面），
        /// 不缓存就是每帧白算一遍缩放和蒙版 —— 实测每帧 0.5 ms 左右，
        /// 150 帧两路就是 150 ms 上下，而这段时间帧顺序锁一直被占着，点开始录制时画面就会顿一下。
        /// 圆角蒙版单独按尺寸缓存：它只跟落位大小有关，实时合成每帧都要用。
        /// </summary>
        private static readonly object PatchCacheLock = new();
        private static readonly List<PreparedPatch> PatchCache = new();
        private static readonly Dictionary<(int Width, int Height), Mat> MaskCache = new();
        private const int MaxCachedPatches = 4;
        private const int MaxCachedMasks = 8;

        /// <summary>一份已经准备好的贴片：缩放、通道对齐、圆角描边都做完了，合成时只剩一次带蒙版拷贝。</summary>
        private sealed class PreparedPatch
        {
            internal Mat? Source;
            internal CameraOverlayRect Rect;
            internal int FrameChannels;
            internal Mat? Patch;

            internal void Dispose()
            {
                Patch?.Dispose();
                Patch = null;
                Source = null;
            }
        }

        /// <summary>
        /// 按给定的落位把副画面贴进主帧，成功返回 true。
        ///
        /// 落位由调用方用 <see cref="CameraOverlayLayout.Resolve"/> 算好再传进来：
        /// 界面上的拖动框、识别框反馈和真正画进帧里的位置必须是**同一个矩形**，
        /// 所以这里不再自己算一份，免得两边的贴角规则（右下/左下/右上/左上）走岔。
        /// 任何一路帧缺失、矩形非法或越出主帧都返回 false 且不改动主帧。
        /// </summary>
        internal static bool TryCompose(
            Mat frame,
            Mat secondaryFrame,
            CameraOverlayRect rect)
        {
            if (frame == null || frame.IsDisposed || frame.Empty())
                return false;
            if (secondaryFrame == null || secondaryFrame.IsDisposed || secondaryFrame.Empty())
                return false;
            if (rect.Width <= 0
                || rect.Height <= 0
                || rect.X < 0
                || rect.Y < 0
                || rect.X + rect.Width > frame.Width
                || rect.Y + rect.Height > frame.Height)
                return false;

            // 取贴片与拷贝都在锁里：贴片可能被新尺寸挤出去释放掉，拷贝必须在它还有效时做完。
            // 拷贝本身只有 0.01 ms 量级，这点串行完全可以接受。
            lock (PatchCacheLock)
            {
                if (RentPatchLocked(secondaryFrame, rect, frame.Channels())?.Patch is not { } patch)
                    return false;

                using var region = new Mat(frame, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
                // 圆角裁剪：四个角保留主画面自己的内容，不能把小窗的方角贴上去。
                patch.CopyTo(region, RentMaskLocked(rect.Width, rect.Height));
                return true;
            }
        }

        /// <summary>按（叠加帧、落位、主帧通道数）取贴片；没命中就现做一份，并把最久没用的挤出去。</summary>
        private static PreparedPatch? RentPatchLocked(Mat secondaryFrame, CameraOverlayRect rect, int frameChannels)
        {
            for (int i = 0; i < PatchCache.Count; i++)
            {
                PreparedPatch cached = PatchCache[i];
                if (!ReferenceEquals(cached.Source, secondaryFrame)
                    || cached.Rect != rect
                    || cached.FrameChannels != frameChannels)
                {
                    continue;
                }

                // 命中就挪到最前：连续回灌时热点一直留在缓存里
                PatchCache.RemoveAt(i);
                PatchCache.Insert(0, cached);
                return cached;
            }

            Mat? patch = BuildPatch(secondaryFrame, rect, frameChannels);
            if (patch is null)
                return null;

            var prepared = new PreparedPatch
            {
                Source = secondaryFrame,
                Rect = rect,
                FrameChannels = frameChannels,
                Patch = patch
            };
            PatchCache.Insert(0, prepared);
            while (PatchCache.Count > MaxCachedPatches)
            {
                PatchCache[^1].Dispose();
                PatchCache.RemoveAt(PatchCache.Count - 1);
            }

            return prepared;
        }

        /// <summary>
        /// 做一份贴片：缩放 + 通道对齐 + 圆角描边。
        /// 边框画在贴片内侧，和以前直接画在主帧上占的像素完全一样。
        /// </summary>
        private static Mat? BuildPatch(Mat secondaryFrame, CameraOverlayRect rect, int frameChannels)
        {
            using var scaled = new Mat();
            Cv2.Resize(
                secondaryFrame,
                scaled,
                new Size(rect.Width, rect.Height),
                interpolation: InterpolationFlags.Area);

            Mat converted = EnsureSameChannels(scaled, frameChannels);
            if (converted.Empty() || converted.Width != rect.Width || converted.Height != rect.Height)
            {
                converted.Dispose();
                return null;
            }

            DrawRoundedBorder(
                converted,
                new Rect(1, 1, rect.Width - 2, rect.Height - 2),
                new Scalar(255, 255, 255),
                BorderThickness,
                ResolveCornerRadius(rect.Width, rect.Height));
            return converted;
        }

        /// <summary>圆角蒙版只跟落位尺寸有关，按尺寸缓存一份就够。</summary>
        private static Mat RentMaskLocked(int width, int height)
        {
            if (MaskCache.TryGetValue((width, height), out Mat? cached) && !cached.IsDisposed)
                return cached;

            Mat mask = BuildRoundedMask(width, height, ResolveCornerRadius(width, height));
            MaskCache[(width, height)] = mask;
            while (MaskCache.Count > MaxCachedMasks)
            {
                (int Width, int Height) oldest = MaskCache.Keys.First();
                MaskCache[oldest].Dispose();
                MaskCache.Remove(oldest);
            }

            return mask;
        }

        /// <summary>
        /// 圆角半径：按小窗短边取比例，保证和识别框的圆角观感一致，不随分辨率跑偏。
        /// 界面上的画中画拖动框也用这个值（换算到屏幕尺寸），两边圆角才对得上。
        /// </summary>
        internal static int ResolveCornerRadius(int width, int height) =>
            Math.Clamp((int)Math.Round(Math.Min(width, height) * 0.02), 4, 48);

        /// <summary>
        /// 圆角矩形蒙版：中间两个十字交叠的矩形加四个实心圆，合成一块圆角形状。
        /// 用来把小窗内容按圆角贴进目标区域，四角留出主画面原本的内容。
        /// </summary>
        private static Mat BuildRoundedMask(int width, int height, int radius)
        {
            var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
            int r = Math.Min(radius, Math.Min(width, height) / 2);
            if (r <= 0)
            {
                mask.SetTo(Scalar.White);
                return mask;
            }

            Cv2.Rectangle(mask, new Rect(r, 0, width - (2 * r), height), Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Rectangle(mask, new Rect(0, r, width, height - (2 * r)), Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(r, r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(width - r, r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(r, height - r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(width - r, height - r), r, Scalar.White, -1, LineTypes.AntiAlias);
            return mask;
        }

        /// <summary>
        /// 画一圈圆角边框：OpenCV 没有现成的圆角矩形，用四段直边加四个 90° 圆弧拼出来。
        /// 识别框是圆角矩形，小窗边框跟着圆角，两者才对得上。
        /// </summary>
        private static void DrawRoundedBorder(Mat frame, Rect rect, Scalar color, int thickness, int radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            if (r <= 0)
            {
                Cv2.Rectangle(frame, rect, color, thickness);
                return;
            }

            int left = rect.Left;
            int top = rect.Top;
            int right = rect.Right;
            int bottom = rect.Bottom;

            Cv2.Line(frame, new Point(left + r, top), new Point(right - r, top), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(left + r, bottom), new Point(right - r, bottom), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(left, top + r), new Point(left, bottom - r), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(right, top + r), new Point(right, bottom - r), color, thickness, LineTypes.AntiAlias);

            Cv2.Ellipse(frame, new Point(left + r, top + r), new Size(r, r), 0, 180, 270, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(right - r, top + r), new Size(r, r), 0, 270, 360, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(right - r, bottom - r), new Size(r, r), 0, 0, 90, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(left + r, bottom - r), new Size(r, r), 0, 90, 180, color, thickness, LineTypes.AntiAlias);
        }

        /// <summary>
        /// 把副画面转成与主帧一致的通道数。两路摄像头后端不同（灰度、BGRA、YUY2 转换结果不同）
        /// 时不能直接 CopyTo，否则 OpenCV 抛异常或写出错位的颜色。
        /// </summary>
        private static Mat EnsureSameChannels(Mat source, int targetChannels)
        {
            if (source.Channels() == targetChannels)
                return source.Clone();

            ColorConversionCodes? conversion = (source.Channels(), targetChannels) switch
            {
                (1, 3) => ColorConversionCodes.GRAY2BGR,
                (1, 4) => ColorConversionCodes.GRAY2BGRA,
                (3, 4) => ColorConversionCodes.BGR2BGRA,
                (4, 3) => ColorConversionCodes.BGRA2BGR,
                _ => null
            };

            if (conversion is null)
                return new Mat();

            var converted = new Mat();
            Cv2.CvtColor(source, converted, conversion.Value);
            return converted;
        }
    }
}
