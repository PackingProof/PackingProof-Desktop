using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 水印绘制器：把每一行文字预渲染成小图（黑描边 + 白字、抗锯齿），之后每帧只做一次叠加。
///
/// 为什么要预渲染：逐字 PutText（描边、填充各一次）在 4K 上要 1.5~2.6 ms/帧，而预录缓存
/// 对每帧开销很敏感。水印文字变化很慢（时间戳每秒一次，单号与扩展行按需变化），所以按
/// “文本 + 字号 + 笔画宽度”缓存渲染结果，每帧只剩一次 Cv2.BlendLinear 叠加：4K 实测
/// 0.27~0.44 ms/帧，抗锯齿表现与逐字绘制一致（描边比字身宽，边缘仍按覆盖率混合）。
/// </summary>
internal sealed class WatermarkOverlayRenderer
{
    /// <summary>行图四周留白：描边比字身宽，留白避免把描边裁掉。</summary>
    internal const int Padding = 8;

    /// <summary>渲染结果的内存上限：长扩展行很占地方，超了就淘汰最久没用到的行。</summary>
    internal const long MaxCacheBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 量字高用的参考串：必须覆盖实际可能画到的极端字形（斜杠、竖线、括号、% 等都比大写字母高），
    /// 否则行图会把它们的上半截裁掉，白字墨迹的上边界跟着变低。
    /// </summary>
    private const string VerticalMetricsReference = "Agjy|/\\()[]{}<>%&@#?*";

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

    internal static int LineHeightOf(double fontScale) => (int)(30 * fontScale / 0.6);

    /// <summary>水印到画面右边缘的距离也按宽度等比：固定 15px 在 4K 上会显得贴边。</summary>
    internal static int MarginOf(int frameWidth) =>
        Math.Max(8, (int)Math.Round(frameWidth * 0.012));

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
            frame.Width - MarginOf(frame.Width) - (entry.Advance * entry.CharacterCount));
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

        var source = new Rect(sourceX, sourceY, width, height);
        using var lineImage = new Mat(entry.Image, source);
        using var lineAlpha = new Mat(entry.Alpha, source);
        using var lineInverseAlpha = new Mat(entry.InverseAlpha, source);
        using var region = new Mat(frame, new Rect(left, top, width, height));
        using var blended = new Mat();
        Cv2.BlendLinear(lineImage, region, lineAlpha, lineInverseAlpha, blended);
        blended.CopyTo(region);
    }

    private Entry? GetOrRender(string text, double fontScale, int thickness, int frameWidth)
    {
        string rendered = SanitizeForHershey(text);
        if (rendered.Length == 0) return null;

        string key = BuildKey(rendered, fontScale, thickness);
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _usage.Remove(node);
                _usage.AddFirst(node);
                return node.Value;
            }
        }

        Entry? created = Render(rendered, fontScale, thickness, frameWidth);
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

        if (!string.Equals(rendered, text, StringComparison.Ordinal))
        {
            RuntimeLog.Warn(
                "Watermark",
                $"水印行含无法用矢量字体绘制的字符，已按 ? 显示：{rendered}");
        }

        return created;
    }

    private static string BuildKey(string text, double fontScale, int thickness) =>
        $"{thickness}|{fontScale:F3}|{text}";

    private static Entry? Render(string text, double fontScale, int thickness, int frameWidth)
    {
        // 等宽步进：取本行最宽字符的宽度（空格不参与）。Hershey 字形本身不等宽，
        // 整串右对齐时秒数一变整行就会左右挪，按最宽字符定步进才能让行首位置只跟字符数有关。
        int advance = 1;
        foreach (char character in text)
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

        // 超长行只渲染装得下的部分，避免一条 1000 字的扩展行把缓存撑爆。
        int maxCharacters = Math.Max(
            1,
            (frameWidth - MarginOf(frameWidth) - Padding) / Math.Max(1, advance));
        int characterCount = Math.Min(text.Length, maxCharacters);
        if (characterCount < text.Length)
            text = text[..characterCount];

        Size box = Cv2.GetTextSize(
            VerticalMetricsReference,
            HersheyFonts.HersheySimplex,
            fontScale,
            thickness,
            out int baseline);
        // GetTextSize 的字体度量对斜杠、竖线、括号这类字形会偏矮，先按宽松画布渲染，
        // 再按真实墨迹裁剪（下面按 coverage 求包围盒），既不会裁掉字形上半截，也不会浪费内存。
        int extraAbove = Math.Max(Padding, (int)Math.Ceiling(fontScale * 16));
        int extraBelow = Math.Max(Padding, (int)Math.Ceiling(fontScale * 6));
        int height = box.Height + extraAbove + extraBelow;
        int width = (advance * characterCount) + (Padding * 2);
        int localBaseline = extraAbove + (box.Height - baseline);

        using var outlineMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        using var fillMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (int i = 0; i < characterCount; i++)
        {
            var position = new Point(Padding + (advance * i), localBaseline);
            string glyph = text[i].ToString();
            Cv2.PutText(
                outlineMask, glyph, position,
                HersheyFonts.HersheySimplex, fontScale, Scalar.White, thickness + 2, LineTypes.AntiAlias);
            Cv2.PutText(
                fillMask, glyph, position,
                HersheyFonts.HersheySimplex, fontScale, Scalar.White, thickness, LineTypes.AntiAlias);
        }

        // 覆盖率（含抗锯齿）= 描边与填充的并集；字身填白、只被描边盖住的地方留黑。
        using var coverage = new Mat();
        Cv2.Max(outlineMask, fillMask, coverage);
        using var inkPoints = new Mat();
        Cv2.FindNonZero(coverage, inkPoints);
        if (inkPoints.Empty())
            return null;

        Rect ink = Cv2.BoundingRect(inkPoints);
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
            advance,
            characterCount,
            ink.X - Padding,
            ink.Y - localBaseline,
            ink.Width,
            ink.Height,
            bytes);
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
                var position = new Point(startX + (advance * i), baseline);
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
            int advance,
            int characterCount,
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
            Advance = advance;
            CharacterCount = characterCount;
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
        internal int Advance { get; }
        internal int CharacterCount { get; }
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
