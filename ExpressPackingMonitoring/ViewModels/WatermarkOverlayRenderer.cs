using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 水印绘制器：把每一行文字预渲染成小图（黑描边 + 白字、抗锯齿），之后每帧只做一次叠加。
///
/// 为什么要预渲染：逐字 PutText（描边、填充各一次）在 4K 上要 1.5~2.6 ms/帧，而预录缓存
/// 对每帧开销很敏感。水印文字变化很慢（时间戳每秒一次，单号与扩展行按需变化），所以按
/// “文本 + 字号”缓存渲染结果，每帧只剩一次 Cv2.BlendLinear 叠加：4K 实测 0.27~0.49 ms/帧。
///
/// 文字用 WPF 真字体（微软雅黑）渲染，不再用 OpenCV 的 Hershey 矢量字体：
/// Hershey 只能画 ASCII，扩展字段里出现中文时那一行会整行画不出来，而且字形本身不等宽、
/// 只能靠逐字摆放凑等宽。字号、行距、右边距都按画面尺寸等比换算，分辨率变大时水印跟着变大。
/// </summary>
internal sealed class WatermarkOverlayRenderer
{
    /// <summary>行图四周留白：描边比字身宽，留白避免把描边裁掉。</summary>
    internal const int Padding = 8;

    /// <summary>渲染结果的内存上限：长扩展行很占地方，超了就淘汰最久没用到的行。</summary>
    internal const long MaxCacheBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 量字高用的参考串：必须覆盖实际可能画到的极端字形（斜杠、竖线、括号、% 等都比大写字母高），
    /// 否则行图会把它们的上半截裁掉。
    /// </summary>
    private const string VerticalMetricsReference = "Agjy|/\\()[]{}<>%&@#?*";

    /// <summary>
    /// 水印字体。实测（见 WatermarkRendererTests 的线程用例）：同一个 Typeface 实例可以被
    /// 不同线程复用、也可以被两个线程并发使用，不会抛线程亲和异常——绘制用的
    /// DrawingVisual / RenderTargetBitmap 是每次调用新建的，本来就各自独立。
    /// </summary>
    private static readonly Typeface WatermarkTypeface = new(
        new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Arial"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _usage = new();
    private long _cachedBytes;

    internal void Draw(Mat frame, IReadOnlyList<string> lines)
    {
        if (frame == null || frame.IsDisposed || frame.Empty() || lines == null || lines.Count == 0)
            return;

        // 只有逐通道 8 位彩色帧才走缓存叠加；其它类型退回落单字绘制，别为了省时间改行为。
        if (frame.Type() != MatType.CV_8UC3)
        {
            DrawLineByLineText(frame, lines);
            return;
        }

        double fontScale = FontScaleOf(frame.Height);
        int thickness = ThicknessOf(fontScale);
        int lineHeight = LineHeightOf(fontScale);

        // 与旧实现保持一致：第一行基线落在 2 倍行高处。
        int lineIndex = 1;
        foreach (string text in lines)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                lineIndex++;
                continue;
            }

            Entry? entry = GetOrRender(text, fontScale, thickness, frame.Width);
            if (entry != null)
                DrawEntry(frame, entry, lineIndex, lineHeight);
            lineIndex++;
        }
    }

    internal static double FontScaleOf(int frameHeight) =>
        Math.Max(0.5, frameHeight / 720.0) * 0.6;

    /// <summary>笔画按画面尺寸等比加粗：固定像素在 4K 上显得又细又虚，在 720p 上又偏粗。</summary>
    internal static int ThicknessOf(double fontScale) =>
        Math.Max(2, (int)Math.Round(fontScale * 3.3));

    /// <summary>
    /// 行距基准：按画面高度等比。这里用四舍五入而不是截断——截断会让 4K 与 1080p 的
    /// 行距比从 2.00 变成 2.02（浮点算出来是 44.999…），字号跟着一起偏。
    /// </summary>
    internal static int LineHeightOf(double fontScale) =>
        (int)Math.Round(30 * fontScale / 0.6, MidpointRounding.AwayFromZero);

    /// <summary>水印到画面右边缘的距离也按宽度等比：固定 15px 在 4K 上会显得贴边。</summary>
    internal static int MarginOf(int frameWidth) =>
        Math.Max(8, (int)Math.Round(frameWidth * 0.012));

    /// <summary>字号跟着行距等比走：分辨率变大时水印跟着变大，不会相对变小。</summary>
    internal static double FontSizeOf(double fontScale) => LineHeightOf(fontScale) * 0.78;

    /// <summary>描边宽度同样按字号等比：固定像素在 4K 上会细得看不出，在 720p 上又会糊。</summary>
    internal static double OutlineWidthOf(double fontSize) => Math.Max(2, fontSize * 0.06);

    /// <summary>量一个字符的推进宽度（等宽判断与单测共用）。</summary>
    internal static double MeasureCharacterAdvance(char character, double fontScale) =>
        MeasureCharacterWidth(character, FontSizeOf(fontScale));

    /// <summary>量一行文字按当前字体排版的宽度（右对齐与单测共用）。</summary>
    internal static double MeasureLineWidth(string text, double fontScale) =>
        CreateFormattedText(text, FontSizeOf(fontScale)).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// Hershey 字体只能画 ASCII：其它字符替换成 <c>?</c>，保证一行画不出来不会连累别的行。
    /// 字段名与命名空间本来就只允许字母数字点划，替换后仍能看出是哪个扩展发的。
    /// </summary>
    internal static string SanitizeForHershey(string text)
    {
        char[]? buffer = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] <= 0x7F) continue;
            buffer ??= text.ToCharArray();
            buffer[i] = '?';
        }

        return buffer == null ? text : new string(buffer);
    }

    private void DrawEntry(Mat frame, Entry entry, int lineIndex, int lineHeight)
    {
        int baseline = (int)(lineHeight * 1.1 * (lineIndex + 1));
        int startX = Math.Max(
            8,
            frame.Width - MarginOf(frame.Width) - entry.LineWidth);
        int left = startX + entry.LeftShift;
        int top = baseline + entry.TopShift;

        // 超长行按旧行为贴边裁剪：能画多少画多少，绝不越界写帧内存。
        int sourceX = 0;
        int sourceY = 0;
        if (left < 0)
        {
            sourceX = -left;
            left = 0;
        }
        if (top < 0)
        {
            sourceY = -top;
            top = 0;
        }

        int width = Math.Min(entry.Width - sourceX, frame.Width - left);
        int height = Math.Min(entry.Height - sourceY, frame.Height - top);
        if (width <= 0 || height <= 0) return;

        var source = new CvRect(sourceX, sourceY, width, height);
        using var lineImage = new Mat(entry.Image, source);
        using var lineAlpha = new Mat(entry.Alpha, source);
        using var lineInverseAlpha = new Mat(entry.InverseAlpha, source);
        using var region = new Mat(frame, new CvRect(left, top, width, height));
        using var blended = new Mat();
        Cv2.BlendLinear(lineImage, region, lineAlpha, lineInverseAlpha, blended);
        blended.CopyTo(region);
    }

    private Entry? GetOrRender(string text, double fontScale, int thickness, int frameWidth)
    {
        if (text.Length == 0) return null;

        string key = BuildKey(text, fontScale, thickness);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _usage.Remove(node);
                _usage.AddFirst(node);
                return node.Value;
            }
        }

        Entry? created = Render(text, fontScale, thickness, frameWidth);
        if (created == null) return null;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                created.Dispose();
                _usage.Remove(existing);
                _usage.AddFirst(existing);
                return existing.Value;
            }

            var node = new LinkedListNode<Entry>(created);
            _usage.AddFirst(node);
            _entries[key] = node;
            _cachedBytes += created.Bytes;
            Trim();
        }

        return created;
    }

    private static string BuildKey(string text, double fontScale, int thickness) =>
        $"{thickness}|{fontScale:F3}|{text}";

    private static Entry? Render(string text, double fontScale, int thickness, int frameWidth)
    {
        double fontSize = FontSizeOf(fontScale);
        double outlineWidth = OutlineWidthOf(fontSize);

        // 自然排版：微软雅黑的数字是等宽数字（实测 0~9 的宽度完全一致），
        // 所以时间戳、单号这类整行宽度在秒数变化时不会变，右对齐也不会左右挪；
        // 逐字强行按最宽字符等距摆放反而会在字母/数字混排时挤出乱间距。
        // 超长行只渲染装得下的部分，避免一条 1000 字的扩展行把缓存撑爆。
        double available = frameWidth - MarginOf(frameWidth) - Padding;
        double used = 0;
        int characterCount = 0;
        double visibleWidth = 0;
        while (characterCount < text.Length)
        {
            double next = MeasureCharacterWidth(text[characterCount], fontSize);
            if (characterCount > 0 && used + next > available) break;
            used += next;
            visibleWidth = used;
            characterCount++;
        }

        // 渲染只画装得下的部分，但缓存键仍用整行原文：否则 Trim 回推的键对不上，条目永远淘汰不掉。
        string renderedText = characterCount < text.Length ? text[..characterCount] : text;

        FormattedText reference = CreateFormattedText(VerticalMetricsReference, fontSize);
        int ascent = (int)Math.Ceiling(reference.Baseline);
        int descent = (int)Math.Ceiling(reference.Height - reference.Baseline);
        int outlinePadding = (int)Math.Ceiling(outlineWidth / 2) + 1;
        int height = ascent + descent + ((outlinePadding + Padding) * 2);
        int width = (int)Math.Ceiling(visibleWidth) + (Padding * 2);
        int localBaseline = Padding + outlinePadding + ascent;

        using Mat outlineMask = RenderMask(
            renderedText, fontSize, localBaseline, width, height, outlineWidth, stroke: true);
        using Mat fillMask = RenderMask(
            renderedText, fontSize, localBaseline, width, height, outlineWidth, stroke: false);

        // 覆盖率（含抗锯齿）= 描边与填充的并集；字身填白、只被描边盖住的地方留黑。
        using var coverage = new Mat();
        Cv2.Max(outlineMask, fillMask, coverage);
        using var inkPoints = new Mat();
        Cv2.FindNonZero(coverage, inkPoints);
        if (inkPoints.Empty())
            return null;

        CvRect ink = Cv2.BoundingRect(inkPoints);
        // 颜色就是白字的覆盖率（抗锯齿边缘是灰的），alpha 才是描边与填充的并集：
        // 这样描边边缘按覆盖率压在画面上，字身边缘用灰度表示自身覆盖率，与逐字绘制的结果一致。
        using var canvasImage = new Mat();
        Cv2.CvtColor(fillMask, canvasImage, ColorConversionCodes.GRAY2BGR);
        using var canvasAlpha = new Mat();
        coverage.ConvertTo(canvasAlpha, MatType.CV_32F, 1.0 / 255.0);
        using var canvasInverseAlpha = new Mat();
        Cv2.Subtract(Scalar.All(1.0), canvasAlpha, canvasInverseAlpha);

        var image = new Mat(canvasImage, ink).Clone();
        var alpha = new Mat(canvasAlpha, ink).Clone();
        var inverseAlpha = new Mat(canvasInverseAlpha, ink).Clone();

        long bytes = (long)image.Total() * image.ElemSize()
            + ((long)alpha.Total() * alpha.ElemSize())
            + ((long)inverseAlpha.Total() * inverseAlpha.ElemSize());
        return new Entry(
            text,
            fontScale,
            thickness,
            image,
            alpha,
            inverseAlpha,
            (int)Math.Ceiling(visibleWidth),
            ink.X - Padding,
            ink.Y - localBaseline,
            ink.Width,
            ink.Height,
            bytes);
    }

    private static FormattedText CreateFormattedText(string text, double fontSize) =>
        new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            WatermarkTypeface,
            fontSize,
            Brushes.White,
            1.0);

    private static double MeasureCharacterWidth(char character, double fontSize) =>
        CreateFormattedText(character.ToString(), fontSize).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// 把一行文字渲染成覆盖率掩码：填充（字身）或描边（黑框那层）单独一遍，
    /// 和旧实现一样，最后用“填充覆盖率当颜色、两层并集当 alpha”合成。
    /// </summary>
    private static Mat RenderMask(
        string text,
        double fontSize,
        int localBaseline,
        int width,
        int height,
        double outlineWidth,
        bool stroke)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            Brush? brush = stroke ? null : Brushes.White;
            var pen = stroke ? new Pen(Brushes.White, outlineWidth) : null;
            double x = Padding;
            foreach (char character in text)
            {
                if (character != ' ')
                {
                    FormattedText formatted = CreateFormattedText(character.ToString(), fontSize);
                    Geometry geometry = formatted.BuildGeometry(new WpfPoint(
                        x,
                        localBaseline - formatted.Baseline));
                    context.DrawGeometry(brush, pen, geometry);
                }

                x += MeasureCharacterWidth(character, fontSize);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        using var bgra = new Mat(height, width, MatType.CV_8UC4);
        for (int y = 0; y < height; y++)
            Marshal.Copy(pixels, y * stride, bgra.Row(y).Data, stride);

        // 白字预乘后 R 通道就是覆盖率（抗锯齿边缘是中间值）。
        var mask = new Mat();
        Cv2.ExtractChannel(bgra, mask, 2);
        return mask;
    }

    /// <summary>按内存上限与条数上限淘汰最久没用到的行缓存。</summary>
    private void Trim()
    {
        while (_usage.Count > 1 && _cachedBytes > MaxCacheBytes)
        {
            LinkedListNode<Entry> last = _usage.Last!;
            _usage.RemoveLast();
            _entries.Remove(
                BuildKey(last.Value.Text, last.Value.FontScale, last.Value.Thickness));
            _cachedBytes -= last.Value.Bytes;
            last.Value.Dispose();
        }
    }

    /// <summary>非 8UC3 帧的兜底路径：逐字绘制，行为与旧实现一致，只是慢。</summary>
    private static void DrawLineByLineText(Mat frame, IReadOnlyList<string> lines)
    {
        double fontScale = FontScaleOf(frame.Height);
        int thickness = ThicknessOf(fontScale);
        int lineHeight = LineHeightOf(fontScale);
        int lineIndex = 1;
        foreach (string text in lines)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                lineIndex++;
                continue;
            }

            string rendered = SanitizeForHershey(text);
            if (!string.Equals(rendered, text, StringComparison.Ordinal))
            {
                RuntimeLog.Warn(
                    "Watermark",
                    $"非 8UC3 画面走了矢量字体兜底，水印里的非 ASCII 字符已按 ? 显示：{rendered}");
            }

            int advance = 1;
            foreach (char character in rendered)
            {
                if (character == ' ') continue;
                advance = Math.Max(
                    advance,
                    Cv2.GetTextSize(
                        character.ToString(),
                        HersheyFonts.HersheySimplex,
                        fontScale,
                        thickness,
                        out _).Width);
            }

            int baseline = (int)(lineHeight * 1.1 * (lineIndex + 1));
            int startX = Math.Max(8, frame.Width - MarginOf(frame.Width) - (advance * rendered.Length));
            for (int i = 0; i < rendered.Length; i++)
            {
                var position = new CvPoint(startX + (advance * i), baseline);
                string glyph = rendered[i].ToString();
                Cv2.PutText(
                    frame, glyph, position,
                    HersheyFonts.HersheySimplex, fontScale, Scalar.Black, thickness + 2, LineTypes.AntiAlias);
                Cv2.PutText(
                    frame, glyph, position,
                    HersheyFonts.HersheySimplex, fontScale, Scalar.White, thickness, LineTypes.AntiAlias);
            }

            lineIndex++;
        }
    }

    private sealed class Entry : IDisposable
    {
        internal Entry(
            string text,
            double fontScale,
            int thickness,
            Mat image,
            Mat alpha,
            Mat inverseAlpha,
            int lineWidth,
            int leftShift,
            int topShift,
            int width,
            int height,
            long bytes)
        {
            Text = text;
            FontScale = fontScale;
            Thickness = thickness;
            Image = image;
            Alpha = alpha;
            InverseAlpha = inverseAlpha;
            LineWidth = lineWidth;
            LeftShift = leftShift;
            TopShift = topShift;
            Width = width;
            Height = height;
            Bytes = bytes;
        }

        /// <summary>淘汰时要按这些字段重建缓存键。</summary>
        internal string Text { get; }
        internal double FontScale { get; }
        internal int Thickness { get; }
        internal Mat Image { get; }
        internal Mat Alpha { get; }
        internal Mat InverseAlpha { get; }
        /// <summary>整行文字宽度（按字体实际排版宽度），用来右对齐。</summary>
        internal int LineWidth { get; }
        /// <summary>行图左上角相对“字身起点（startX）”的水平偏移。</summary>
        internal int LeftShift { get; }
        /// <summary>行图左上角相对基线的垂直偏移。</summary>
        internal int TopShift { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal long Bytes { get; }

        public void Dispose()
        {
            Image.Dispose();
            Alpha.Dispose();
            InverseAlpha.Dispose();
        }
    }
}
