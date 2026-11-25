using ExpressPackingMonitoring.Services.Gpu;
using OpenCvSharp;
using System.Windows.Media.Imaging;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 把叠加画面帧转成取景编辑预览位图。先按主预览同一套规则缩到显示尺寸，
    /// 再从 Mat 的原生缓冲区直接创建已冻结位图，避免每帧分配整张托管像素数组。
    /// </summary>
    internal static class OverlayPreviewBitmapFactory
    {
        internal static BitmapSource Create(Mat frame, int displayWidth)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.IsDisposed || frame.Empty() || frame.Type() != MatType.CV_8UC3)
                throw new ArgumentException("Overlay preview requires a live BGR24 frame.", nameof(frame));

            Mat? resized = null;
            Mat? continuous = null;
            try
            {
                Mat source = frame;
                (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(
                    frame.Width,
                    frame.Height,
                    displayWidth);
                if (target is { } size)
                {
                    resized = new Mat();
                    Cv2.Resize(
                        frame,
                        resized,
                        new Size(size.Width, size.Height),
                        interpolation: GpuPreviewResizer.ResolveInterpolation(
                            frame.Width,
                            frame.Height,
                            size.Width,
                            size.Height));
                    source = resized;
                }

                if (!source.IsContinuous())
                {
                    // ROI 的最后一列未必铺满整行 stride；复制成连续图后再交给 WIC，避免尾部越界读。
                    continuous = source.Clone();
                    source = continuous;
                }

                int stride = checked((int)source.Step());
                int bufferSize = checked(stride * source.Height);
                var bitmap = BitmapSource.Create(
                    source.Width,
                    source.Height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgr24,
                    null,
                    source.Data,
                    bufferSize,
                    stride);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                continuous?.Dispose();
                resized?.Dispose();
            }
        }
    }
}
