using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebRequestLimitTests
{
    [Fact]
    public void ListenerTimeouts_BoundSlowHeadersBodiesAndIdleConnections()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), WebServer.RequestHeaderWaitTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), WebServer.RequestEntityBodyTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), WebServer.IdleConnectionTimeout);
    }

    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("secret-key", "SECRET-KEY", false)]
    [InlineData("", "secret-key", false)]
    public void AccessKeysEqual_UsesExactComparison(string left, string right, bool expected)
    {
        Assert.Equal(expected, WebServer.AccessKeysEqual(left, right));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void ShouldServeClipInline_OnlyAcceptsExplicitFlag(string? value, bool expected)
    {
        Assert.Equal(expected, WebServer.ShouldServeClipInline(value));
    }

    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=900-", 1000, 900, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=0-9999", 1000, 0, 999)]
    public void TryResolveByteRange_AcceptsSingleValidRange(
        string header,
        long fileLength,
        long expectedStart,
        long expectedEnd)
    {
        Assert.True(WebServer.TryResolveByteRange(header, fileLength, out long start, out long end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("bytes=1000-", 1000)]
    [InlineData("bytes=20-10", 1000)]
    [InlineData("bytes=0-1,3-4", 1000)]
    [InlineData("items=0-10", 1000)]
    [InlineData("bytes=-0", 1000)]
    [InlineData("bytes=0-0", 0)]
    public void TryResolveByteRange_RejectsMalformedOrUnsatisfiedRange(string header, long fileLength)
    {
        Assert.False(WebServer.TryResolveByteRange(header, fileLength, out _, out _));
    }

    [Fact]
    public void ValidateOrderInfoItems_AcceptsBoundarySizedBatch()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems)
            .Select(index => new OrderInfo
            {
                TrackingNumber = $"TRACK-{index}",
                BuyerMessage = new string('买', 2000),
                SellerMemo = new string('卖', 2000),
                ProductInfo = new string('商', 4000)
            })
            .ToList();

        WebServer.ValidateOrderInfoItems(items);
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsTooManyOrders()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems + 1)
            .Select(index => new OrderInfo { TrackingNumber = index.ToString() })
            .ToList();

        Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsOversizedField()
    {
        var items = new List<OrderInfo>
        {
            new() { TrackingNumber = "TRACK-1", BuyerMessage = new string('x', 2001) }
        };

        var error = Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));

        Assert.Contains("买家留言过长", error.Message);
    }

    [Fact]
    public void ClipEditor_UsesSingleScreenSourcePlaybackWorkflow()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("id=\"clipSourcePlayer\"", html);
        Assert.Contains("id=\"clipPlayhead\"", html);
        Assert.Contains("id=\"clipPlaySelectionBtn\"", html);
        Assert.Contains("id=\"clipEditorBody\"", html);
        Assert.Contains("生成并下载", html);
        Assert.Contains("resolvePlaybackUrl(v.id)", html);
        Assert.Contains("function applyClipSourceLayout(width,height)", html);
        Assert.Contains("body.clip-open{overflow:hidden}", html);
        Assert.Contains("height:min(1080px,calc(100dvh - 32px))", html);
        Assert.Contains(".clip-editor-body{min-height:0;flex:1 1 auto;overflow:hidden}", html);
        Assert.Contains(".clip-actions{position:relative;bottom:auto;flex:0 0 auto;", html);
        Assert.Contains(".clip-actions .btn>[data-icon]{display:grid;width:19px;height:19px", html);
        Assert.Contains(".clip-actions .btn .icon{display:block;width:19px;height:19px", html);
        Assert.Contains(".clip-editor-body[data-orientation=\"portrait\"]", html);
        Assert.Contains(".clip-info{grid-template-columns:repeat(3,minmax(0,1fr))", html);
        Assert.Contains(".clip-summary-item:first-child{display:none}", html);
        Assert.Contains("padding:12px 18px 18px", html);
        Assert.Contains("手机录像、扫码与备份", html);
        Assert.DoesNotContain("id=\"clipResult\"", html);
        Assert.DoesNotContain("/clip/preview", html);
        Assert.DoesNotContain("/clip/frame", html);
        Assert.DoesNotContain("/clip/prewarm", html);
    }

    [Fact]
    public void VideoList_UsesLazyThumbnailsAndSourceBadges()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("thumb.loading='lazy'", html);
        Assert.Contains("thumb.decoding='async'", html);
        Assert.Contains("loadThumbnailWithRetry(thumb,v.thumbnailUrl)", html);
        Assert.Contains("attempt>=3||!thumb.isConnected", html);
        Assert.Contains("v.thumbnailUrl", html);
        Assert.Contains("id=\"mobileConnectButton\"", html);
        Assert.Contains("data-icon=\"phoneDesktop\"", html);
        Assert.Contains("data-icon=\"integration\"", html);
        Assert.Contains("external device-color-'+sourceDeviceColor(v)", html);
        Assert.Contains("status.textContent=sourceDeviceDisplayName(v)", html);
        Assert.Contains("add('录像来源',sourceDeviceDisplayName(v))", html);
        Assert.Contains("'录像来源':'Recording source'", html);
        Assert.Contains("'全部设备':'All devices'", html);
        Assert.Contains(".replace(/^手机(\\d+)$/g,'Phone $1')", html);
        Assert.Contains(".replace(/^电脑(\\d+)$/g,'PC $1')", html);
        Assert.Contains("match=/^手机(\\d+)$/", html);
        Assert.Contains("return '设备 '+id.slice(-6)", html);
        Assert.Contains("tagLine.className='tag-line'", html);
        Assert.Contains("grid-template-columns:minmax(320px,1.7fr) minmax(300px,1.4fr) auto", html);
        Assert.Contains(".topbar{display:flex;align-items:center;", html);
        Assert.Contains("orderLine.append(order)", html);
        Assert.Contains("tagLine.append(badge,status)", html);
        Assert.Contains("missingBadge.className='missing-badge'", html);
        Assert.Contains("missingBadge.textContent='文件丢失'", html);
        Assert.Contains(".status-badge{background:var(--status-bg);color:var(--ok)", html);
        Assert.Contains(".status-badge.external{background:var(--external-status-bg);color:var(--external-status-text)}", html);
        Assert.Contains("text('resultsInfo','第 '+res.page+' / '+totalPages+' 页')", html);
        Assert.DoesNotContain("'共 '+res.total+' 条记录", html);
        Assert.Contains(".mobile-connect-toggle,.install-card{display:none}", html);
        Assert.Contains("class=\"mobile-app-download\"", html);
        Assert.Contains(
            "href=\"https://gitee.com/PackingProof/PackingProof-Mobile/releases/latest\"",
            html);
        Assert.Contains("@media (min-width:561px) and (max-width:900px)", html);
        Assert.Contains(".mobile-app-download{grid-column:1;grid-row:1}", html);
        Assert.Contains("grid-template-columns:minmax(0,1fr) 56px 56px 56px", html);
        Assert.Contains(".mobile-app-download-title{font-size:13px;font-weight:760;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}", html);
        Assert.Contains("grid-template-columns:minmax(0,1fr) 56px 56px 56px 56px", html);
        Assert.Contains(".title-block h1{margin:0;font-size:26px;line-height:1.2;font-weight:750;white-space:nowrap}", html);
        Assert.Contains("text-decoration:none;justify-self:end}", html);
        Assert.Contains("id=\"desktopAppDownloadButton\" type=\"button\"", html);
        Assert.Contains("id=\"desktopAppDownloadCopy\"", html);
        Assert.Contains(".app-download-actions{display:flex;gap:8px;margin-top:11px}", html);
        Assert.Contains("const target=document.querySelector('.top-actions');", html);
        Assert.Contains("titleBlock.appendChild(btn)", html);
        Assert.Contains(".title-block{display:flex;align-items:center;justify-content:space-between", html);
        Assert.Contains("grid-template-columns:minmax(0,1fr) 56px 56px}", html);
        Assert.Contains(".top-actions>.language-float .language-trigger .icon{width:22px;height:22px}", html);
        Assert.Contains("id=\"desktopAppDownloadQr\"", html);
        Assert.Contains("fetch('/api/mobile-app-download'", html);
        Assert.Contains("fetch('/api/mobile-app-download'+(location.search||'')", html);
        Assert.Contains("let downloadUrl=open?open.href:''", html);
        Assert.Contains(".floating-tools{display:none}", html);
        Assert.Contains("if(window.matchMedia&&window.matchMedia('(max-width:900px)').matches)return", html);
    }

    [Fact]
    public void VideoSearch_ProvidesDatabaseBackedSourceFilter()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("id=\"videoSource\"", html);
        Assert.Contains("button,input,select{font:inherit}", html);
        Assert.Contains("<option value=\"\">全部设备</option>", html);
        Assert.Contains("fetch('/api/video-sources'", html);
        Assert.Contains("select.replaceChildren(new Option('全部设备',''))", html);
        Assert.Contains("sourceType,deviceId,sourceName,page:currentPage", html);
    }

    [Fact]
    public void PlaybackHeaderLinksProjectAndInstallCardLinksGuide()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains(
            "<a href=\"https://gitee.com/PackingProof\" target=\"_blank\" rel=\"noopener\">快递打包录像回放</a>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "class=\"install-card\" href=\"/kuaidizs-install-guide\"",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileOverview_UsesCompactCardsAndAutoCodecDetection()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("@media (max-width:820px){.overview{grid-template-columns:repeat(2,minmax(0,1fr))", html);
        Assert.Contains(".overview .summary-card:nth-child(3){display:none}", html);
        Assert.Contains(".overview #oldestNote,.overview #retentionNote{display:none}", html);
        Assert.DoesNotContain("<p>按日期或订单号检索局域网监控端录像", html);
        Assert.Contains("playbackCompatChoice='auto'", html);
        Assert.Contains("compatChoiceStorageKey='expressPlaybackCompatChoice'", html);
        Assert.Contains("id=\"compatSettingsButton\"", html);
        Assert.Contains("id=\"compatSettingsMenu\"", html);
        Assert.Contains("<circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M19.4 15a1.65", html);
        Assert.Contains("input type=\"radio\" name=\"compatChoice\"", html);
        Assert.Contains("function probeCodecSupport()", html);
        Assert.Contains("canPlayType", html);
        Assert.Contains("hvc1.1.6.L120.90", html);
        Assert.Contains("hev1.1.6.L120.90", html);
        Assert.Contains("av01.0.04M.08", html);
        Assert.Contains("function compatValueFor(codec,forced)", html);
        Assert.Contains("自动切换为兼容播放", html);
        Assert.DoesNotContain("expressPlaybackCompatMode", html);
        Assert.DoesNotContain("compatModeToggle", html);
        Assert.Contains(".floating-tools{position:fixed;right:max(16px,env(safe-area-inset-right));top:min(84vh,calc(100vh - 110px))", html);
        Assert.Contains("max-height:calc(100vh - 32px);overflow:auto", html);
        Assert.Contains("function paginationWindowSize(){return window.matchMedia('(max-width:560px)').matches?3:window.matchMedia('(max-width:900px)').matches?5:9}", html);
        Assert.Contains("refreshResponsivePagination()", html);
        Assert.Contains(".page-btn{flex:0 0 auto}", html);
    }

    [Fact]
    public void LanguagePickerUsesAccessibleFloatingMenuInsteadOfNativeSelect()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("wrap.id='languageFloat'", html);
        Assert.Contains("trigger.id='languageTrigger'", html);
        Assert.Contains("menu.id='languageMenu'", html);
        Assert.Contains("trigger.setAttribute('aria-expanded','false')", html);
        Assert.Contains("option.setAttribute('role','menuitemradio')", html);
        Assert.Contains("option.setAttribute('aria-checked'", html);
        Assert.Contains("event.key==='ArrowDown'", html);
        Assert.Contains("event.key==='Escape'&&menu.classList.contains('open')", html);
        Assert.Contains("localStorage.removeItem(key)", html);
        Assert.Contains("localStorage.setItem(key,value)", html);
        Assert.Contains("syncTarget=()=>{const target=document.querySelector('.top-actions')", html);
        Assert.Contains("window.addEventListener('resize',syncTarget)", html);
        Assert.Contains(".top-actions>.language-float{position:relative;display:flex}", html);
        Assert.DoesNotContain("document.createElement('select')", html);
        Assert.DoesNotContain("select.style.cssText", html);
    }
}
