using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Drawing;
using System.Drawing.Imaging;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 摄像头位图转 OpenCV Mat。
    ///
    /// 只做像素格式转换，绝不重采样：GDI+ 的 <c>Graphics.DrawImage</c> 默认带半像素偏移，
    /// 会把整帧重新插值一遍 —— 每帧都糊一点、颜色也偏一点，镜像套镜像时一层层叠加，
    /// 肉眼就是"越套越糊、越套越灰"。Bitmap.Clone 只换格式、不动几何，是无损的。
    /// </summary>
    internal static class CameraFrameConverter
    {
        private static int _loggedFrameFormat;

        internal static Mat ConvertToBgrMat(Bitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            LogIncomingFrameFormatOnce(bitmap);

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            if (bitmap.PixelFormat == PixelFormat.Format24bppRgb)
            {
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    return Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, data.Scan0, data.Stride).Clone();
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }

            // 其它格式（32bpp、索引、16bpp 等）先做纯格式转换，再按 24bpp 直拷。
            using Bitmap converted = bitmap.Clone(rect, PixelFormat.Format24bppRgb);
            BitmapData convertedData = converted.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                return Mat.FromPixelData(converted.Height, converted.Width, MatType.CV_8UC3, convertedData.Scan0, convertedData.Stride).Clone();
            }
            finally
            {
                converted.UnlockBits(convertedData);
            }
        }

        /// <summary>
        /// 记录一次摄像头帧的像素格式：现场遇到"预览发糊/偏色"时，
        /// 先确认走的是哪条转换分支（24bpp 直拷，还是需要格式转换）。
        /// </summary>
        private static void LogIncomingFrameFormatOnce(Bitmap bitmap)
        {
            PixelFormat format = bitmap.PixelFormat;
            if (Interlocked.Exchange(ref _loggedFrameFormat, 1) != 0)
                return;

            RuntimeLog.Info("Camera", $"Camera frame format={format}, size={bitmap.Width}x{bitmap.Height}");
        }
    }
}
