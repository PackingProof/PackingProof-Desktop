#nullable disable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExpressPackingMonitoring.Helpers
{
    /// <summary>
    /// 简易 Code 128B 条形码生成器，纯 WPF 实现，无外部依赖。
    /// </summary>
    public static class BarcodeHelper
    {
        internal const int QuietZoneModules = 10;

        // Code 128B 编码表：每个字符对应 6 组宽度（bar, space, bar, space, bar, space）
        private static readonly int[][] Patterns = {
            new[]{2,1,2,2,2,2}, new[]{2,2,2,1,2,2}, new[]{2,2,2,2,2,1}, new[]{1,2,1,2,2,3},
            new[]{1,2,1,3,2,2}, new[]{1,3,1,2,2,2}, new[]{1,2,2,2,1,3}, new[]{1,2,2,3,1,2},
            new[]{1,3,2,2,1,2}, new[]{2,2,1,2,1,3}, new[]{2,2,1,3,1,2}, new[]{2,3,1,2,1,2},
            new[]{1,1,2,2,3,2}, new[]{1,2,2,1,3,2}, new[]{1,2,2,2,3,1}, new[]{1,1,3,2,2,2},
            new[]{1,2,3,1,2,2}, new[]{1,2,3,2,2,1}, new[]{2,2,3,2,1,1}, new[]{2,2,1,1,3,2},
            new[]{2,2,1,2,3,1}, new[]{2,1,3,2,1,2}, new[]{2,2,3,1,1,2}, new[]{3,1,2,1,3,1},
            new[]{3,1,1,2,2,2}, new[]{3,2,1,1,2,2}, new[]{3,2,1,2,2,1}, new[]{3,1,2,2,1,2},
            new[]{3,2,2,1,1,2}, new[]{3,2,2,2,1,1}, new[]{2,1,2,1,2,3}, new[]{2,1,2,3,2,1},
            new[]{2,3,2,1,2,1}, new[]{1,1,1,3,2,3}, new[]{1,3,1,1,2,3}, new[]{1,3,1,3,2,1},
            new[]{1,1,2,3,2,3/*SPACE*/}, new[]{1,3,2,1,2,3/*SPACE*/}, new[]{1,3,2,3,2,1},
            new[]{2,1,1,3,2,3}, new[]{2,3,1,1,2,3}, new[]{2,3,1,3,2,1}, new[]{1,1,2,1,3,3},
            new[]{1,1,2,3,3,1}, new[]{1,3,2,1,3,1}, new[]{1,1,3,1,2,3}, new[]{1,1,3,3,2,1},
            new[]{1,3,3,1,2,1}, new[]{3,1,3,1,2,1}, new[]{2,1,1,3,3,1}, new[]{2,3,1,1,3,1},
            new[]{2,1,3,1,1,3}, new[]{2,1,3,3,1,1}, new[]{2,1,3,1,3,1}, new[]{3,1,1,1,2,3},
            new[]{3,1,1,3,2,1}, new[]{3,3,1,1,2,1}, new[]{3,1,2,1,1,3}, new[]{3,1,2,3,1,1},
            new[]{3,3,2,1,1,1}, new[]{3,1,4,1,1,1}, new[]{2,2,1,4,1,1}, new[]{4,3,1,1,1,1},
            new[]{1,1,1,2,2,4}, new[]{1,1,1,4,2,2}, new[]{1,2,1,1,2,4}, new[]{1,2,1,4,2,1},
            new[]{1,4,1,1,2,2}, new[]{1,4,1,2,2,1}, new[]{1,1,2,2,1,4}, new[]{1,1,2,4,1,2},
            new[]{1,2,2,1,1,4}, new[]{1,2,2,4,1,1}, new[]{1,4,2,1,1,2}, new[]{1,4,2,2,1,1},
            new[]{2,4,1,2,1,1}, new[]{2,2,1,1,1,4}, new[]{4,1,3,1,1,1}, new[]{2,4,1,1,1,2},
            new[]{1,3,4,1,1,1}, new[]{1,1,1,2,4,2}, new[]{1,2,1,1,4,2}, new[]{1,2,1,2,4,1},
            new[]{1,1,4,2,1,2}, new[]{1,2,4,1,1,2}, new[]{1,2,4,2,1,1}, new[]{4,1,1,2,1,2},
            new[]{4,2,1,1,1,2}, new[]{4,2,1,2,1,1}, new[]{2,1,2,1,4,1}, new[]{2,1,4,1,2,1},
            new[]{4,1,2,1,2,1}, new[]{1,1,1,1,4,3}, new[]{1,1,1,3,4,1}, new[]{1,3,1,1,4,1},
            new[]{1,1,4,1,1,3}, new[]{1,1,4,3,1,1}, new[]{4,1,1,1,1,3}, new[]{4,1,1,3,1,1},
            new[]{1,1,3,1,4,1}, new[]{1,1,4,1,3,1}, new[]{3,1,1,1,4,1}, new[]{4,1,1,1,3,1},
            new[]{2,1,1,4,1,2}, new[]{2,1,1,2,1,4}, new[]{2,1,1,2,3,2},
        };
        // Stop pattern: 2 3 3 1 1 1 2
        private static readonly int[] StopPattern = { 2, 3, 3, 1, 1, 1, 2 };

        /// <summary>
        /// 生成 Code 128B 条形码为 BitmapSource（WPF 可直接绑定）。
        /// </summary>
        public static BitmapSource Generate(string text, int height = 60, int moduleWidth = 2)
        {
            if (string.IsNullOrEmpty(text)) text = " ";

            // 编码字符值列表（Code 128B: value = ascii - 32）
            var values = new List<int>();
            foreach (char c in text)
            {
                int v = c - 32;
                if (v < 0 || v > 94) v = 0; // 不可编码字符替换为空格
                values.Add(v);
            }

            // 计算校验位
            int startCode = 104; // Start B
            int checksum = startCode;
            for (int i = 0; i < values.Count; i++)
                checksum += values[i] * (i + 1);
            int checkValue = checksum % 103;

            // 构建完整编码序列：Start B + data + checksum + Stop
            var allCodes = new List<int> { startCode };
            allCodes.AddRange(values);
            allCodes.Add(checkValue);

            // 计算总宽度
            int totalModules = CalculateTotalModules(allCodes);
            double dpiScale = GetDpiScale();
            (int pixelWidth, int pixelHeight, double moduleWidthDip, double heightDip) =
                CalculateRasterMetrics(totalModules, height, moduleWidth, dpiScale);
            Brush background = ResolveBrush("BarcodeBackground");
            Brush foreground = ResolveBrush("BarcodeForeground");

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, pixelWidth / dpiScale, heightDip));

                double x = QuietZoneModules * moduleWidthDip;
                // Draw all code patterns
                foreach (var code in allCodes)
                    x = DrawPattern(dc, Patterns[code], x, heightDip, moduleWidthDip, foreground);
                // Draw stop pattern
                DrawPattern(dc, StopPattern, x, heightDip, moduleWidthDip, foreground);
            }

            double dpi = 96 * dpiScale;
            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        internal static int CalculateTotalModules(IEnumerable<int> codes)
        {
            int totalModules = QuietZoneModules * 2;
            foreach (int code in codes)
                foreach (int width in Patterns[code])
                    totalModules += width;
            foreach (int width in StopPattern)
                totalModules += width;
            return totalModules;
        }

        internal static (int PixelWidth, int PixelHeight, double ModuleWidthDip, double HeightDip)
            CalculateRasterMetrics(int totalModules, int height, int moduleWidth, double dpiScale)
        {
            dpiScale = dpiScale > 0 ? dpiScale : 1;
            int physicalModuleWidth = Math.Max(1, (int)Math.Floor(moduleWidth * dpiScale));
            int pixelHeight = Math.Max(1, (int)Math.Round(height * dpiScale));
            return (
                totalModules * physicalModuleWidth,
                pixelHeight,
                physicalModuleWidth / dpiScale,
                pixelHeight / dpiScale);
        }

        private static double GetDpiScale()
        {
            Visual visual = Application.Current?.MainWindow;
            return visual == null ? 1 : VisualTreeHelper.GetDpi(visual).DpiScaleX;
        }

        private static Brush ResolveBrush(string resourceKey) =>
            Application.Current?.TryFindResource(resourceKey) as Brush
            ?? throw new InvalidOperationException($"Missing brush resource: {resourceKey}");

        private static double DrawPattern(
            DrawingContext dc,
            int[] pattern,
            double x,
            double height,
            double moduleWidth,
            Brush foreground)
        {
            bool isBar = true;
            foreach (var w in pattern)
            {
                double pixelWidth = w * moduleWidth;
                if (isBar)
                    dc.DrawRectangle(foreground, null, new Rect(x, 0, pixelWidth, height));
                x += pixelWidth;
                isBar = !isBar;
            }
            return x;
        }
    }
}
