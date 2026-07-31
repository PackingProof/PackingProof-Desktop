using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebRequestLimitTests
{
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
        Assert.Contains(".clip-actions .btn .icon{width:19px;height:19px", html);
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
        Assert.Contains("v.thumbnailUrl", html);
        Assert.Contains("id=\"mobileConnectButton\"", html);
        Assert.Contains("data-icon=\"phoneDesktop\"", html);
        Assert.Contains("data-icon=\"integration\"", html);
        Assert.Contains("external device-color-'+sourceDeviceColor(v)", html);
        Assert.Contains("status.textContent=external?sourceDeviceDisplayName(v):'电脑'", html);
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
            "href=\"https://gitee.com/PackingProof/PackingProof-Mobile/releases\"",
            html);
        Assert.Contains(".mobile-app-download{display:flex}", html);
        Assert.Contains(".mobile-app-download{grid-column:1/-1;grid-row:2}", html);
        Assert.Contains("id=\"desktopAppDownloadQr\"", html);
        Assert.Contains("fetch('/api/mobile-app-download'", html);
        Assert.Contains(".floating-tools{display:none}", html);
        Assert.Contains("if(window.matchMedia&&window.matchMedia('(max-width:900px)').matches)return", html);
    }

    [Fact]
    public void VideoSearch_ProvidesDatabaseBackedSourceFilter()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("id=\"videoSource\"", html);
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
    public void MobileOverview_UsesCompactCardsAndDisablesCompatibilityOnFirstVisit()
    {
        string html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "index.html"));

        Assert.Contains("@media (max-width:820px){.overview{grid-template-columns:repeat(2,minmax(0,1fr))", html);
        Assert.Contains(".overview .summary-card:nth-child(3){display:none}", html);
        Assert.Contains(".overview #oldestNote,.overview #retentionNote{display:none}", html);
        Assert.DoesNotContain("<p>按日期或订单号检索局域网监控端录像", html);
        Assert.Contains("localStorage.getItem(compatStorageKey)===null", html);
        Assert.Contains("window.matchMedia('(max-width:900px)').matches", html);
        Assert.Contains("localStorage.setItem(compatStorageKey,'0')", html);
        Assert.Contains(".floating-tools{position:fixed;right:max(16px,env(safe-area-inset-right));top:min(70vh,calc(100vh - 190px))", html);
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
        Assert.Contains("syncTarget=()=>{const mobile=window.matchMedia&&window.matchMedia('(max-width:900px)').matches", html);
        Assert.Contains("window.addEventListener('resize',syncTarget)", html);
        Assert.Contains(".top-actions>.language-float{position:relative;display:flex}", html);
        Assert.DoesNotContain("document.createElement('select')", html);
        Assert.DoesNotContain("select.style.cssText", html);
    }
}
