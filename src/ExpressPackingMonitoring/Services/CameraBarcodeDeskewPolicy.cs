using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 斜放面单的倾角估计与摆正。
///
/// 现有解码链路只有 0/90/180/270 四个朝向（ReusableGrayLuminanceSource），
/// 而一维码的扫描线是水平的：条码一旦斜着放，条纹方向和扫描线不重合，
/// ZXing 直接读不出来，现场表现就是"必须把面单摆正才能识别"。
///
/// 这里先用形态学把条码条纹横向连成整块，再用最小外接矩形估出长轴与
/// 水平线的夹角，交给调用方绕中心旋转摆正。
///
/// 刻意不引入目标检测模型：这个仓库是纯 .NET 桌面程序，装机体积和
/// 第三方依赖都要克制，而"条码是引导框里最长的那条长条"这个先验
/// 在引导框范围内已经足够稳。
/// </summary>
internal static class CameraBarcodeDeskewPolicy
{
    /// <summary>小于这个角度不扶正：条码本来就是正的，多转只会引入插值模糊。</summary>
    internal const double MinAngle = 3.0;

    /// <summary>大于这个角度不扶正：更接近竖直，而竖直朝向已有朝向循环兜底。</summary>
    internal const double MaxAngle = 45.0;

    /// <summary>梯度方向直方图的桶宽（度）。</summary>
    private const double BucketSize = 2.0;

    /// <summary>直方图桶数：180 度 / 2 度。</summary>
    private const int BucketCount = 90;

    /// <summary>
    /// 峰值桶的权重占比下限：低于这个值说明画面里没有成组的条纹，
    /// 只是些零散纹理，扶正只会把画面转歪。
    /// </summary>
    private const double MinConcentration = 0.12;

    /// <summary>
    /// 估算 ROI 里条码长轴与水平线的夹角（度）。
    ///
    /// 图像坐标系的 y 轴向下，正角表示长轴顺时针倾斜。返回 null 表示
    /// "不该扶正"：要么本来就是正的，要么接近竖直（朝向循环已经处理），
    /// 要么画面里根本没有成组的条纹。
    ///
    /// 做法是统计**梯度方向直方图**：一维码是等距排列的竖条，竖条边缘
    /// 产生的梯度方向高度一致，峰值方向就是条码长轴方向。
    ///
    /// 两个关键取舍：
    ///   ① 用原灰度图而不是二值图 —— 二值化后斜条纹会变成阶梯，
    ///      阶梯的横竖边把方向打散，小角度（十几度）根本估不出来；
    ///      灰度图的梯度没有这个问题，而且与黑白极性无关。
    ///   ② 早先用"找长条轮廓的最小外接矩形"估角，实测会被背景的大块
    ///      轮廓带偏（白底矩形本身也是一条长边很长的斜矩形）。
    /// </summary>
    internal static double? EstimateSkewAngle(Mat gray)
    {
        if (gray == null || gray.IsDisposed || gray.Empty())
            return null;

        using Mat gx = new();
        using Mat gy = new();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);

        int step = Math.Max(1, Math.Max(gray.Width, gray.Height) / 600);
        var weights = new double[BucketCount];
        double total = 0;

        for (int y = 0; y < gray.Height; y += step)
        {
            for (int x = 0; x < gray.Width; x += step)
            {
                double mx = gx.At<float>(y, x);
                double my = gy.At<float>(y, x);
                double magnitude = Math.Sqrt(mx * mx + my * my);
                if (magnitude <= 0)
                    continue;
                double degrees = Math.Atan2(my, mx) * 180.0 / Math.PI;
                if (degrees < 0)
                    degrees += 180.0;   // 无向：0~180 就够了
                int bin = (int)(degrees / BucketSize);
                if (bin >= BucketCount)
                    bin = BucketCount - 1;
                weights[bin] += magnitude;
                total += magnitude;
            }
        }

        if (total <= 0)
            return null;

        int peak = 0;
        for (int i = 1; i < BucketCount; i++)
        {
            if (weights[i] > weights[peak])
                peak = i;
        }

        // 峰值太散说明画面里没有成组的条纹，别乱扶正。
        if (weights[peak] / total < MinConcentration)
            return null;

        // 方向是无向的：0° 和 180° 是同一头，首尾两桶要合并算，
        // 合并后落在 0° 附近（水平），而不是 90°（竖直）。
        double axis;
        if (peak == 0 || peak == BucketCount - 1)
        {
            double head = weights[0];
            double tail = weights[BucketCount - 1];
            double sum = head + tail;
            if (sum <= 0)
                return null;
            // 首桶中心算 +1°，尾桶中心算 -1°。
            axis = (head * 1.0 - tail * 1.0) / sum;
        }
        else
        {
            // 峰值桶连同左右邻居一起加权：只用桶中心会有 ±1° 的量化误差。
            int from = Math.Max(1, peak - 2);
            int to = Math.Min(BucketCount - 2, peak + 2);
            double sumDegrees = 0;
            double sumWeight = 0;
            for (int i = from; i <= to; i++)
            {
                sumDegrees += weights[i] * ((i + 0.5) * BucketSize);
                sumWeight += weights[i];
            }
            if (sumWeight <= 0)
                return null;
            double center = sumDegrees / sumWeight;
            axis = center > 90 ? center - 180 : center;
        }

        if (Math.Abs(axis) < MinAngle || Math.Abs(axis) > MaxAngle)
            return null;
        return axis;
    }

    /// <summary>
    /// 摆正后的输出尺寸：原图绕中心旋转后能装下整张图的外接矩形。
    ///
    /// 必须按外接矩形算，不能用 1/cos 估：面单是长条形的，
    /// 旋转后高度方向也变长了，只放大宽边会把条码上下切掉。
    /// </summary>
    internal static void EstimateDeskewedSize(
        int width,
        int height,
        double angle,
        out int outWidth,
        out int outHeight)
    {
        double radians = angle * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(radians));
        double sin = Math.Abs(Math.Sin(radians));
        outWidth = Math.Max(1, (int)Math.Ceiling(width * cos + height * sin));
        outHeight = Math.Max(1, (int)Math.Ceiling(width * sin + height * cos));
    }

    /// <summary>
    /// 绕中心把图摆正。输出是旋转后的外接矩形，中心保持对齐。
    /// 插值用双线性：最近邻会把一维码的细条纹采成宽窄不均，ZXing 直接读不出。
    /// </summary>
    internal static Mat? Deskew(Mat source, double angle)
    {
        if (source == null || source.IsDisposed || source.Empty())
            return null;

        EstimateDeskewedSize(source.Width, source.Height, angle, out int outWidth, out int outHeight);

        double centerX = source.Width / 2.0;
        double centerY = source.Height / 2.0;
        double outCenterX = outWidth / 2.0;
        double outCenterY = outHeight / 2.0;

        using Mat matrix = Cv2.GetRotationMatrix2D(
            new Point2f((float)centerX, (float)centerY), angle, 1.0);
        // 输出尺寸变了，中心要跟着挪，否则摆正后条码整体偏移。
        matrix.At<double>(0, 2) += outCenterX - centerX;
        matrix.At<double>(1, 2) += outCenterY - centerY;

        Mat result = new();
        Cv2.WarpAffine(
            source,
            result,
            matrix,
            new Size(outWidth, outHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            // 补白而不是补黑：ZXing 扫一维码是逐行找黑白交替，
            // 补出来的黑边会在某些行里形成假的"黑-白-黑"，把真条码糊掉。
            Scalar.White);
        return result;
    }
}
