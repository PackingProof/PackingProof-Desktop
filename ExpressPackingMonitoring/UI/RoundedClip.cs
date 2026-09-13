using System;
using System.Windows;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 给容器套一个跟随尺寸的圆角裁剪。
    /// WPF 的 ClipToBounds 只按矩形裁剪，不认 Border 的 CornerRadius，
    /// 所以视频这类铺满的内容会从圆角处溢出直角。附加属性写一次，主界面和小窗共用。
    /// </summary>
    public static class RoundedClip
    {
        /// <summary>圆角半径；大于 0 时自动维护 Clip，随尺寸变化重算。</summary>
        public static readonly DependencyProperty RadiusProperty =
            DependencyProperty.RegisterAttached(
                "Radius",
                typeof(CornerRadius),
                typeof(RoundedClip),
                new PropertyMetadata(default(CornerRadius), OnRadiusChanged));

        public static void SetRadius(DependencyObject element, CornerRadius value) =>
            element.SetValue(RadiusProperty, value);

        public static CornerRadius GetRadius(DependencyObject element) =>
            (CornerRadius)element.GetValue(RadiusProperty);

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;

            element.SizeChanged -= OnSizeChanged;
            element.SizeChanged += OnSizeChanged;
            ApplyClip(element);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
            ApplyClip((FrameworkElement)sender);

        private static void ApplyClip(FrameworkElement element)
        {
            double width = element.ActualWidth;
            double height = element.ActualHeight;
            CornerRadius radius = GetRadius(element);

            if (width <= 0 || height <= 0)
            {
                element.Clip = null;
                return;
            }

            element.Clip = BuildGeometry(width, height, radius);
        }

        /// <summary>四个角可以各自不同，底部接状态条时就把下面两角设为 0。</summary>
        internal static Geometry BuildGeometry(double width, double height, CornerRadius radius)
        {
            // 半径不能超过半边长，否则弧线会自相交画出奇怪的形状。
            double maxRadius = Math.Min(width, height) / 2;
            double topLeft = Clamp(radius.TopLeft, maxRadius);
            double topRight = Clamp(radius.TopRight, maxRadius);
            double bottomRight = Clamp(radius.BottomRight, maxRadius);
            double bottomLeft = Clamp(radius.BottomLeft, maxRadius);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(0, topLeft), isFilled: true, isClosed: true);
                Arc(context, new Point(topLeft, 0), topLeft);
                context.LineTo(new Point(width - topRight, 0), isStroked: false, isSmoothJoin: false);
                Arc(context, new Point(width, topRight), topRight);
                context.LineTo(new Point(width, height - bottomRight), isStroked: false, isSmoothJoin: false);
                Arc(context, new Point(width - bottomRight, height), bottomRight);
                context.LineTo(new Point(bottomLeft, height), isStroked: false, isSmoothJoin: false);
                Arc(context, new Point(0, height - bottomLeft), bottomLeft);
            }
            geometry.Freeze();
            return geometry;
        }

        private static void Arc(StreamGeometryContext context, Point to, double radius)
        {
            if (radius <= 0)
            {
                context.LineTo(to, isStroked: false, isSmoothJoin: false);
                return;
            }

            context.ArcTo(
                to,
                new Size(radius, radius),
                0,
                false,
                SweepDirection.Clockwise,
                isStroked: false,
                isSmoothJoin: false);
        }

        private static double Clamp(double radius, double maxRadius) =>
            Math.Max(0, Math.Min(radius, maxRadius));
    }
}
