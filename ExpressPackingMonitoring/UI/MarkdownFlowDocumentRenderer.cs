using ExpressPackingMonitoring.Logging;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI
{
    internal static class MarkdownFlowDocumentRenderer
    {
        public static FlowDocument Render(string markdown)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 12,
                LineHeight = 18
            };

            if (string.IsNullOrWhiteSpace(markdown))
            {
                document.Blocks.Add(CreateParagraph("暂无更新说明。"));
                return document;
            }

            string normalized = NormalizeHtmlImages(markdown.Trim());
            string[] lines = normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (Regex.IsMatch(line.Trim(), @"^-{3,}$"))
                {
                    document.Blocks.Add(new Paragraph(new Run("------------")) { Margin = new Thickness(0, 8, 0, 8) });
                    continue;
                }

                Match heading = Regex.Match(line, @"^(#{1,3})\s+(.+)$");
                if (heading.Success)
                {
                    int level = heading.Groups[1].Value.Length;
                    var paragraph = CreateParagraph(heading.Groups[2].Value);
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = level == 1 ? 15 : level == 2 ? 14 : 13;
                    paragraph.Margin = new Thickness(0, level == 1 ? 2 : 8, 0, 6);
                    document.Blocks.Add(paragraph);
                    continue;
                }

                Match bullet = Regex.Match(line.TrimStart(), @"^[-*]\s+(.+)$");
                if (bullet.Success)
                {
                    var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(16, 0, 0, 4) };
                    list.ListItems.Add(new ListItem(CreateParagraph(bullet.Groups[1].Value)));
                    document.Blocks.Add(list);
                    continue;
                }

                document.Blocks.Add(CreateParagraph(line));
            }

            return document;
        }

        private static Paragraph CreateParagraph(string text)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 6)
            };

            foreach (Inline inline in CreateInlines(text))
                paragraph.Inlines.Add(inline);

            return paragraph;
        }

        private static Inline[] CreateInlines(string text)
        {
            var inlines = new System.Collections.Generic.List<Inline>();
            var pattern = new Regex(@"\*\*(.+?)\*\*|\[([^\]]+)\]\((https?://[^)]+)\)|(https?://\S+)");
            int lastIndex = 0;

            foreach (Match match in pattern.Matches(text))
            {
                if (match.Index > lastIndex)
                    inlines.Add(new Run(text[lastIndex..match.Index]));

                if (match.Groups[1].Success)
                {
                    inlines.Add(new Bold(new Run(match.Groups[1].Value)));
                }
                else
                {
                    string label = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
                    string url = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                    var hyperlink = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url) };
                    hyperlink.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            Services.UpdateCheckService.OpenDownloadPage(e.Uri.ToString());
                        }
                        catch (Exception ex)
                        {
                            RuntimeLog.Error("Update", "Open markdown link failed", ex);
                        }
                    };
                    inlines.Add(hyperlink);
                }

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
                inlines.Add(new Run(text[lastIndex..]));

            return inlines.ToArray();
        }

        private static string NormalizeHtmlImages(string markdown)
        {
            return Regex.Replace(
                markdown,
                "<img\\s+[^>]*?alt=\"(?<alt>[^\"]*)\"[^>]*?src=\"(?<src>https?://[^\"]+)\"[^>]*?>",
                match =>
                {
                    string alt = match.Groups["alt"].Value;
                    string src = match.Groups["src"].Value;
                    string label = string.IsNullOrWhiteSpace(alt) ? "图片" : $"图片：{alt}";
                    return $"[{label}]({src})";
                },
                RegexOptions.IgnoreCase);
        }
    }
}
