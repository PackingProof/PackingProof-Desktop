import assert from 'node:assert/strict';
import test from 'node:test';
import { chromium } from 'playwright-core';

const baseUrl = process.env.EPM_AUTOMATION_BASE_URL;

// 二维码格式随宿主不同：WPF 宿主输出 PNG，无头自动化宿主与 macOS 备份主机输出内置 SVG。
// 断言只要求"真的渲染出一张二维码图片"，不锁死格式；同时确认图片已经解码成功。
const qrDataUri = /^data:image\/(?:png|svg\+xml);base64,/;
async function waitForQr(page, selector) {
  await assert.doesNotReject(() => page.waitForFunction(
    ({ selector, pattern }) => {
      const image = document.querySelector(selector);
      return !!image
        && new RegExp(pattern).test(image.getAttribute('src') || '')
        && image.complete
        && image.naturalWidth > 0;
    },
    { selector, pattern: qrDataUri.source }
  ));
}

async function assertDownloadActionsWithinCards(page) {
  const cards = await page.locator('.app-download-platform').evaluateAll(platforms =>
    platforms.map(platform => {
      const rect = platform.getBoundingClientRect();
      return {
        left: rect.left,
        right: rect.right,
        buttons: [...platform.querySelectorAll('.app-download-actions .btn')].map(button => {
          const box = button.getBoundingClientRect();
          return {
            left: box.left,
            right: box.right,
            top: box.top,
            bottom: box.bottom,
            scrollWidth: button.scrollWidth,
            clientWidth: button.clientWidth
          };
        })
      };
    }));

  assert.equal(cards.length, 2);
  for (const card of cards) {
    assert.equal(card.buttons.length, 2);
    for (const button of card.buttons) {
      assert.ok(button.left >= card.left - 1, 'download action should stay inside its platform card');
      assert.ok(button.right <= card.right + 1, 'download action should not overflow its platform card');
      assert.ok(button.scrollWidth <= button.clientWidth + 1, 'download action text should not be clipped');
    }
    assert.ok(
      card.buttons[0].bottom <= card.buttons[1].top + 1
        || card.buttons[1].bottom <= card.buttons[0].top + 1,
      'download action buttons must not overlap'
    );
  }
}

async function assertHeaderTitleNotTruncated(page) {
  const metrics = await page.locator('.title-block h1 a').evaluate(link => ({
    clientWidth: link.clientWidth,
    scrollWidth: link.scrollWidth,
    text: link.innerText
  }));
  assert.ok(
    metrics.scrollWidth <= metrics.clientWidth + 1,
    `header title is truncated despite available space: ${metrics.text}`
  );
}

async function assertLongOrderNumberStaysOnOneLineInsideCard(orderNumber) {
  const originalText = await orderNumber.innerText();
  const longNumber = 'AUTO_WEB_LONG_ORDER_1234567890_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0987654321';
  await orderNumber.evaluate((element, value) => {
    element.textContent = value;
    element.style.removeProperty('font-size');
    element.style.removeProperty('transform');
    if (typeof fitVideoOrderNumber === 'function') fitVideoOrderNumber(element);
  }, longNumber);
  const metrics = await orderNumber.evaluate(element => {
    const style = getComputedStyle(element);
    const orderRect = element.getBoundingClientRect();
    const item = element.closest('.video-item');
    const itemRect = item?.getBoundingClientRect();
    const metadata = item?.querySelector('.meta-grid');
    const metadataRect = metadata?.getBoundingClientRect();
    const actions = item?.querySelector('.video-actions');
    const actionsRect = actions?.getBoundingClientRect();
    return {
      whiteSpace: style.whiteSpace,
      textOverflow: style.textOverflow,
      lineHeight: Number.parseFloat(style.lineHeight),
      height: element.getBoundingClientRect().height,
      orderRight: orderRect.right,
      orderBottom: orderRect.bottom,
      metadataLeft: metadataRect?.left ?? Number.POSITIVE_INFINITY,
      itemRight: itemRect?.right ?? Number.NEGATIVE_INFINITY,
      actionsTop: actionsRect?.top ?? Number.POSITIVE_INFINITY
    };
  });
  assert.equal(metrics.whiteSpace, 'nowrap', 'long order number must stay on one line');
  assert.equal(metrics.textOverflow, 'clip', 'long order number should not render an ellipsis');
  assert.ok(metrics.height <= metrics.lineHeight * 1.5, 'long order number should remain a single line');
  assert.ok(metrics.orderRight > metrics.metadataLeft, 'long order number should extend across the metadata area');
  assert.ok(metrics.orderRight <= metrics.itemRight + 1, 'long order number should stay inside the card');
  assert.ok(metrics.orderBottom <= metrics.actionsTop + 1, 'order number must not overlap action buttons');
  await orderNumber.evaluate((element, value) => {
    element.textContent = value;
    element.style.removeProperty('font-size');
    element.style.removeProperty('transform');
    if (typeof fitVideoOrderNumber === 'function') fitVideoOrderNumber(element);
  }, originalText);
}

test('long order number stays inside the card without covering actions', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'zh-CN' });
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(String(error)));
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await page.waitForFunction(() => document.querySelectorAll('.video-item .order-id').length > 0);

    await assertLongOrderNumberStaysOnOneLineInsideCard(page.locator('.order-id').first());
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(
      () => requestAnimationFrame(resolve))));
    await assertLongOrderNumberStaysOnOneLineInsideCard(page.locator('.order-id').first());
    assert.deepEqual(pageErrors, [], '页面不应有未捕获的脚本异常');
  } finally {
    await browser.close();
  }
});

test('isolated Web server supports search, playback and clip editor entry', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'zh-CN' });
    const page = await context.newPage();
    // 页面脚本未捕获异常必须直接失败：初始化中途抛错时，后面的列表/搜索都不会执行
    const pageErrors = [];
    let clipTimelineRequests = 0;
    page.on('pageerror', error => pageErrors.push(String(error)));
    page.on('request', request => {
      if (new URL(request.url()).pathname.endsWith('/clip/timeline')) clipTimelineRequests++;
    });
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'PackingProof 包裹留证' }).waitFor());
    await page.waitForFunction(() => /^第 \d+ \/ \d+ 页$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));

    // 存储盘在线时不得出现拔盘提示条
    assert.equal(await page.locator('#storageBanner').isVisible(), false);

    // 筛选面板：按钮呼出、条件即时生效、徽章与导出状态联动
    const filterButton = page.locator('#filterButton');
    const filterPanel = page.locator('#filterPanel');
    const exportButton = page.locator('#exportOrdersButton');
    assert.equal(await exportButton.isDisabled(), false);
    assert.equal(await filterPanel.isVisible(), false);
    await filterButton.click();
    await assert.doesNotReject(() => filterPanel.waitFor({ state: 'visible' }));
    assert.equal(await filterButton.getAttribute('aria-expanded'), 'true');
    const modeFilterResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/videos'
        && url.searchParams.get('mode') === 'return'
        && response.ok();
    });
    await page.locator('#modeFilter').selectOption('return');
    await modeFilterResponse;
    await assert.doesNotReject(() => page.locator('#filterBadges .filter-badge').filter({ hasText: '退货' }).waitFor());
    assert.equal(await page.locator('#filterCount').innerText(), '1');
    await page.keyboard.press('Escape');
    assert.equal(await filterPanel.isVisible(), false);
    assert.equal(await filterButton.getAttribute('aria-expanded'), 'false');
    await filterButton.click();
    await page.locator('#clearFiltersButton').click();
    await assert.doesNotReject(() => page.locator('#filterBadges').waitFor({ state: 'hidden' }));
    assert.equal(await page.locator('#filterCount').isVisible(), false);
    await page.keyboard.press('Escape');

    // 当前筛选没有结果时导出单号不可用
    const keywordInput = page.locator('#keyword');
    await keywordInput.fill('AUTO_WEB_NO_MATCH');
    await keywordInput.press('Enter');
    await page.waitForFunction(() => document.querySelector('#resultsInfo')?.textContent?.trim()
      === '没有找到匹配记录');
    assert.equal(await exportButton.isDisabled(), true);
    await keywordInput.fill('');
    await keywordInput.press('Enter');
    await page.waitForFunction(() => /^第 \d+ \/ \d+ 页$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));

    const appDownloadButton = page.getByRole('button', { name: '下载 苹果/安卓版' });
    await appDownloadButton.click();
    await assert.doesNotReject(() => page.locator('#desktopAppDownloadPopover.open').waitFor());
    await waitForQr(page, '#desktopAndroidDownloadQr');
    await waitForQr(page, '#desktopIosDownloadQr');
    await assertDownloadActionsWithinCards(page);
    assert.match(
      await page.locator('#desktopAndroidDownloadOpen').getAttribute('href'),
      /gitee\.com\/PackingProof\/PackingProof-Mobile\/releases\/latest/
    );
    assert.match(
      await page.locator('#desktopIosDownloadOpen').getAttribute('href'),
      /testflight\.apple\.com\/join\//
    );
    assert.equal(await appDownloadButton.getAttribute('aria-expanded'), 'true');
    await page.keyboard.press('Escape');
    assert.equal(await appDownloadButton.getAttribute('aria-expanded'), 'false');

    const floatingPosition = await page.locator('#floatingTools').evaluate(element => {
      const rect = element.getBoundingClientRect();
      return { centerY: rect.top + rect.height / 2, viewportHeight: window.innerHeight };
    });
    assert.ok(floatingPosition.centerY > floatingPosition.viewportHeight * 0.6);
    assert.ok(floatingPosition.centerY < floatingPosition.viewportHeight - 150);

    const mobileConnectButton = page.getByRole('button', { name: '手机打开' });
    await assert.doesNotReject(() => mobileConnectButton.waitFor());
    await mobileConnectButton.click();
    await assert.doesNotReject(() => page.locator('#mobileConnectOverlay.active').waitFor());
    await waitForQr(page, '#mobileConnectQr');
    assert.equal(await page.locator('#mobileConnectUrl').inputValue(), `http://192.168.1.20:${new URL(baseUrl).port}`);
    await page.keyboard.press('Escape');
    await page.setViewportSize({ width: 811, height: 900 });
    assert.equal(await mobileConnectButton.isVisible(), false);
    await assert.doesNotReject(() => page.getByRole('link', { name: '下载手机 App' }).waitFor());
    assert.equal(await page.locator('#mobileConnectQr').isVisible(), false);
    assert.equal(await page.locator('#desktopAndroidDownloadQr').isVisible(), false);
    assert.equal(await page.locator('#desktopIosDownloadQr').isVisible(), false);
    assert.equal(
      await page.locator('#languageFloat').evaluate(element => element.parentElement?.classList.contains('top-actions')),
      true
    );
    const compactOverview = await page.locator('.overview').evaluate(overview => {
      const cards = overview.querySelectorAll('.summary-card');
      return {
        columns: getComputedStyle(overview).gridTemplateColumns.split(' ').length,
        storageDisplay: getComputedStyle(cards[2]).display,
        oldestNoteDisplay: getComputedStyle(document.querySelector('#oldestNote')).display,
        retentionNoteDisplay: getComputedStyle(document.querySelector('#retentionNote')).display
      };
    });
    assert.deepEqual(compactOverview, {
      columns: 2,
      storageDisplay: 'none',
      oldestNoteDisplay: 'none',
      retentionNoteDisplay: 'none'
    });
    await page.evaluate(() => {
      document.getElementById('pagination').innerHTML = '';
      renderPagination(10, 20);
    });
    // 窗口宽度变化会安排一次 requestAnimationFrame 分页重绘；等目标帧完成后再计数。
    await page.waitForFunction(
      () => document.querySelectorAll('#pagination .page-btn').length === 7
    );
    await page.setViewportSize({ width: 1280, height: 900 });

    const androidContext = await browser.newContext({
      locale: 'zh-CN',
      userAgent: 'Mozilla/5.0 (Linux; Android 15; Mobile) AppleWebKit/537.36 Chrome/127 Mobile Safari/537.36'
    });
    const mobilePage = await androidContext.newPage();
    await mobilePage.setViewportSize({ width: 390, height: 844 });
    let mobileDownloadInfoRequests = 0;
    mobilePage.on('request', request => {
      if (new URL(request.url()).pathname === '/api/mobile-app-download') mobileDownloadInfoRequests++;
    });
    await mobilePage.goto(baseUrl, { waitUntil: 'networkidle' });
    const androidDownload = mobilePage.getByRole('link', { name: '下载 Android App' });
    assert.equal(await androidDownload.isVisible(), true);
    assert.match(
      await androidDownload.getAttribute('href'),
      /gitee\.com\/PackingProof\/PackingProof-Mobile\/releases\/latest/
    );
    assert.equal(await mobilePage.locator('#desktopAndroidDownloadQr').isVisible(), false);
    assert.equal(mobileDownloadInfoRequests, 0);
    await mobilePage.evaluate(() => {
      document.getElementById('pagination').innerHTML = '';
      renderPagination(10, 20);
    });
    const mobilePageButtons = mobilePage.locator('#pagination .page-btn');
    assert.equal(await mobilePageButtons.count(), 5);
    assert.equal(await mobilePageButtons.first().innerText(), '上一页');
    assert.equal(await mobilePageButtons.last().innerText(), '下一页');
    const paginationBounds = await mobilePage.locator('#pagination').evaluate(element => ({
      scrollWidth: element.scrollWidth,
      clientWidth: element.clientWidth
    }));
    assert.ok(paginationBounds.scrollWidth <= paginationBounds.clientWidth);
    await mobilePage.close();
    await androidContext.close();

    const iosContext = await browser.newContext({
      locale: 'zh-CN',
      userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1'
    });
    const iosPage = await iosContext.newPage();
    await iosPage.setViewportSize({ width: 390, height: 844 });
    await iosPage.goto(baseUrl, { waitUntil: 'networkidle' });
    const iosDownload = iosPage.getByRole('link', { name: '加入 iOS 内测' });
    assert.equal(await iosDownload.isVisible(), true);
    assert.match(await iosDownload.getAttribute('href'), /testflight\.apple\.com\/join\//);
    assert.equal(await iosPage.evaluate(() => detectMobileAppPlatform(
      'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15)',
      'MacIntel',
      5
    )), 'ios');
    assert.equal(await iosPage.evaluate(() => detectMobileAppPlatform(
      'Mozilla/5.0 (X11; Linux x86_64)',
      'Linux x86_64',
      0
    )), 'unknown');
    await iosContext.close();

    const search = page.getByPlaceholder('输入订单号或文件名搜索');
    await search.fill('AUTO_WEB_001');
    const searchResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/videos'
        && url.searchParams.get('keyword') === 'AUTO_WEB_001'
        && response.ok();
    });
    await search.press('Enter');
    await searchResponse;
    await page.waitForFunction(() => /^第 \d+ \/ \d+ 页$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));
    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());
    assert.match(await page.locator('#resultsInfo').innerText(), /^第 \d+ \/ \d+ 页$/);
    await assertLongOrderNumberStaysOnOneLineInsideCard(page.locator('.order-id').first());
    await page.setViewportSize({ width: 390, height: 844 });
    await assertLongOrderNumberStaysOnOneLineInsideCard(page.locator('article .order-id').first());
    await page.setViewportSize({ width: 1280, height: 900 });

    await article.getByRole('button', { name: '播放', exact: true }).click();
    await assert.doesNotReject(() => page.locator('#playerOverlay.active').waitFor());
    assert.equal(await page.locator('.page').evaluate(element => element.inert), true);
    await page.waitForFunction(() => /\/api\/videos\/\d+\/play/.test(
      document.querySelector('#videoPlayer')?.getAttribute('src') || ''));
    const source = await page.locator('#videoPlayer').getAttribute('src');
    assert.match(source ?? '', /\/api\/videos\/\d+\/play/);
    await page.waitForFunction(() => document.querySelector('#playerTitle')?.textContent?.includes(
      'H.264 · 直接播放原片'));
    await page.waitForFunction(() => {
      const player = document.querySelector('#videoPlayer');
      return player?.readyState >= 2 && player?.paused === false;
    });
    await page.locator('#videoPlayer').press('Space');
    await page.waitForFunction(() => document.querySelector('#videoPlayer')?.paused === true);
    assert.equal(await page.locator('#playerOverlay').isVisible(), true);
    assert.equal(await page.locator('#videoPlayer').getAttribute('src'), source);
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('.page').evaluate(element => element.inert), false);

    await article.getByRole('button', { name: '剪辑' }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
    await page.waitForFunction(() => typeof clipState !== 'undefined' && clipState?.sourceReady === true);
    assert.equal(await page.locator('.page').evaluate(element => element.inert), true);
    assert.match(await page.locator('#clipEstimateSize').innerText(), /MB|GB/);
    await page.waitForFunction(() => document.querySelectorAll('#clipFrameTrack .clip-frame.pending').length === 0);
    const initialPlayhead = await page.evaluate(() => clipState.playhead);
    await page.evaluate(() => document.querySelector('article button[aria-label="剪辑"]').focus());
    await page.keyboard.press('Space');
    await page.waitForFunction(() => clipState?.selectionPlaying === true);
    assert.ok(await page.evaluate(() => clipState.playhead) >= initialPlayhead - 0.08);
    assert.equal(await page.locator('#clipOverlay').isVisible(), true);
    await page.keyboard.press('Space');
    await page.waitForFunction(() => clipState?.selectionPlaying === false);
    const timelineRequestsBeforeSpace = clipTimelineRequests;
    await page.evaluate(() => seekClipSource(Math.min(1.1, Math.max(0, clipState.total - 0.3))));
    const playheadBeforeSpace = await page.evaluate(() => clipState.playhead);
    await page.locator('#clipPlayhead').focus();
    await page.keyboard.press('Space');
    await page.waitForFunction(() => clipState?.selectionPlaying === true);
    await page.waitForTimeout(300);
    assert.equal(clipTimelineRequests, timelineRequestsBeforeSpace);
    assert.ok(await page.evaluate(() => clipState.playhead) >= playheadBeforeSpace - 0.08);
    assert.equal(await page.locator('#clipError').isVisible(), false);
    await page.keyboard.press('Space');
    await page.waitForFunction(() => clipState?.selectionPlaying === false);
    await page.setViewportSize({ width: 1760, height: 1272 });
    await page.evaluate(() => applyClipSourceLayout(1920, 1080));
    const desktopClipGeometry = await page.locator('#clipOverlay').evaluate(overlay => {
      const dialog = overlay.querySelector('.clip-dialog').getBoundingClientRect();
      const body = overlay.querySelector('#clipEditorBody');
      const playButton = overlay.querySelector('#clipPlaySelectionBtn').getBoundingClientRect();
      const playIcon = overlay.querySelector('#clipPlaySelectionBtn .icon').getBoundingClientRect();
      const qualityButton = overlay.querySelector('#clipQualityButton').getBoundingClientRect();
      return {
        dialogHeight: dialog.height,
        bodyClientHeight: body.clientHeight,
        bodyScrollHeight: body.scrollHeight,
        playIconWidth: playIcon.width,
        playCenterDelta: Math.abs((playButton.top + playButton.height / 2) - (playIcon.top + playIcon.height / 2)),
        qualityButtonHeight: qualityButton.height,
        qualityButtonWidth: qualityButton.width
      };
    });
    assert.ok(desktopClipGeometry.dialogHeight > 820);
    assert.ok(desktopClipGeometry.bodyScrollHeight <= desktopClipGeometry.bodyClientHeight + 1);
    assert.ok(desktopClipGeometry.playIconWidth >= 19);
    assert.ok(desktopClipGeometry.playCenterDelta <= 1);
    assert.ok(desktopClipGeometry.qualityButtonHeight >= 40);
    assert.ok(desktopClipGeometry.qualityButtonWidth >= 112);
    await page.evaluate(() => applyClipSourceLayout(1080, 1920));
    assert.equal(await page.locator('#clipMainPreview').getAttribute('data-orientation'), 'portrait');
    assert.equal(await page.locator('body').evaluate(element => element.classList.contains('clip-open')), true);
    await page.setViewportSize({ width: 390, height: 844 });
    const portraitClipGeometry = await page.locator('#clipOverlay').evaluate(overlay => {
      const preview = overlay.querySelector('#clipMainPreview').getBoundingClientRect();
      const actions = overlay.querySelector('.clip-actions').getBoundingClientRect();
      const quality = overlay.querySelector('#clipQualityButton').getBoundingClientRect();
      return {
        previewHeight: preview.height,
        previewWidth: preview.width,
        actionsTop: actions.top,
        actionsBottom: actions.bottom,
        qualityTop: quality.top,
        qualityBottom: quality.bottom,
        viewportHeight: window.innerHeight
      };
    });
    assert.ok(portraitClipGeometry.previewHeight > 300);
    assert.ok(portraitClipGeometry.previewWidth <= 620);
    assert.ok(portraitClipGeometry.actionsTop >= 0);
    assert.ok(portraitClipGeometry.actionsBottom <= portraitClipGeometry.viewportHeight);
    assert.ok(portraitClipGeometry.qualityTop >= 0);
    assert.ok(portraitClipGeometry.qualityBottom <= portraitClipGeometry.viewportHeight);
    const compactInfo = await page.locator('#clipEditorBody').evaluate(body => ({
      infoColumns: getComputedStyle(body.querySelector('.clip-info')).gridTemplateColumns.split(' ').length,
      timeFieldsDisplay: getComputedStyle(body.querySelector('.clip-time-fields')).display,
      summaryDisplay: getComputedStyle(body.querySelector('.clip-summary')).display,
      durationDisplay: getComputedStyle(body.querySelector('.clip-summary-item.duration')).display,
      estimateDisplay: getComputedStyle(body.querySelector('.clip-summary-item.estimate')).display,
      qualityDisplay: getComputedStyle(body.querySelector('#clipQualityButton')).display
    }));
    assert.deepEqual(compactInfo, {
      infoColumns: 1,
      timeFieldsDisplay: 'grid',
      summaryDisplay: 'grid',
      durationDisplay: 'none',
      estimateDisplay: 'flex',
      qualityDisplay: 'flex'
    });
    await page.locator('#clipCloseBtn').click();
    assert.equal(await page.locator('body').evaluate(element => element.classList.contains('clip-open')), false);

    const videoId = await article.evaluate(element => videoTipData.get(element)?.id);
    assert.ok(Number.isInteger(videoId) && videoId > 0);
    await page.goto(
      `${baseUrl}?action=clip&videoId=${videoId}&orderId=AUTO_WEB_001&fileName=fixture.mp4&durationSec=2&videoCodec=h264`,
      { waitUntil: 'networkidle' }
    );
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
    await page.waitForFunction(() => typeof clipState !== 'undefined' && clipState?.sourceReady === true);
    assert.equal(await page.evaluate(() => Number(clipState?.video?.id)), videoId);
    assert.equal(await page.evaluate(() => Number(clipState?.total)) > 0, true);
    await page.locator('#clipCloseBtn').click();
    assert.deepEqual(pageErrors, [], '页面不应有未捕获的脚本异常');
  } finally {
    await browser.close();
  }
});

test('Web UI follows browser language and persists an explicit override', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'en-US' });
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(String(error)));
    await page.addInitScript(() => {
      if (!localStorage.getItem('expressWebLanguage')) localStorage.setItem('expressWebLanguage', 'en-US');
    });
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'PackingProof package evidence' }).waitFor());
    assert.equal(await page.locator('html').getAttribute('lang'), 'en');
    assert.equal(await page.title(), 'PackingProof package evidence');
    await assertHeaderTitleNotTruncated(page);
    await page.setViewportSize({ width: 1000, height: 900 });
    await assertHeaderTitleNotTruncated(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    // 本次新增的筛选与导出文案必须有英文，英文界面不能残留中文
    assert.equal(await page.locator('#keyword').getAttribute('placeholder'), 'Search by order number');
    assert.doesNotMatch(await page.locator('.toolbar').innerText(), /[\u3400-\u9fff]/);
    await page.locator('#filterButton').click();
    await assert.doesNotReject(() => page.locator('#filterPanel').waitFor({ state: 'visible' }));
    assert.doesNotMatch(await page.locator('#filterPanel').innerText(), /[\u3400-\u9fff]/);
    const filterOptions = await page.locator('#filterPanel select option').allInnerTexts();
    assert.ok(filterOptions.length > 0);
    assert.doesNotMatch(filterOptions.join(' '), /[\u3400-\u9fff]/);
    assert.equal(await page.locator('#filterButton').getAttribute('title'), 'Filter');
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#filterPanel').isVisible(), false);
    await page.locator('#compatSettingsButton').click();
    await assert.doesNotReject(() => page.locator('#compatSettingsMenu').filter({ hasText: 'Playback compatibility' }).waitFor());
    await assert.doesNotReject(() => page.locator('#compatSettingsMenu input[name="compatChoice"][value="transcode"]').waitFor());

    const search = page.getByPlaceholder('Search by order number');
    await search.fill('AUTO_WEB_001');
    const searchResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/videos'
        && url.searchParams.get('keyword') === 'AUTO_WEB_001'
        && response.ok();
    });
    await search.press('Enter');
    await searchResponse;
    await page.waitForFunction(() => /^Page \d+ of \d+$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));
    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());
    assert.match(await page.locator('#resultsInfo').innerText(), /^Page \d+ of \d+$/);

    await article.hover();
    await assert.doesNotReject(() => page.locator('#videoTip').waitFor({ state: 'visible' }));
    const tooltipLabels = await page.locator('#videoTip .video-tip-label').allInnerTexts();
    assert.ok(tooltipLabels.length > 0);
    assert.doesNotMatch(tooltipLabels.join(' '), /[\u3400-\u9fff]/);
    assert.ok(tooltipLabels.some(label => label === 'Tracking number' || label === 'Order number'));

    await assert.doesNotReject(() => article.getByRole('button', { name: 'Play', exact: true }).waitFor());
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Download' }).waitFor());
    assert.doesNotMatch(await article.innerText(), /发货|退货|文件存在|文件丢失|播放|下载/);
    assert.equal(await page.locator('#startDate').getAttribute('type'), 'text');
    assert.equal(await page.locator('#startDate').getAttribute('placeholder'), 'YYYY-MM-DD');

    await page.evaluate(() => renderPagination(2, 3));
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Previous' }).waitFor());
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Next' }).waitFor());

    await page.evaluate(() => renderStorageOverview({}));
    assert.doesNotMatch(await page.locator('.overview').innerText(), /[\u3400-\u9fff]/);
    await page.evaluate(() => showExportProgress('正在准备导出', 2, 10));
    const exportProgressText = await page.locator('#exportProgress').innerText();
    assert.match(exportProgressText, /Preparing export/);
    assert.match(exportProgressText, /Processed 2 \/ 10/);
    assert.doesNotMatch(exportProgressText, /[\u3400-\u9fff]/);
    await page.evaluate(() => hideExportProgress());

    await article.getByRole('button', { name: 'Clip', exact: true }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
    assert.doesNotMatch(await page.locator('#clipOverlay').innerText(), /[\u3400-\u9fff]/);
    await page.locator('#clipQualityButton').click();
    await assert.doesNotReject(() => page.locator('#clipQualityOverlay.active').waitFor());
    assert.equal(await page.locator('#clipOverlay').evaluate(element => element.inert), true);
    assert.equal(await page.locator('[data-quality-mode]').count(), 0);
    await page.waitForFunction(() => document.querySelector('#clipQualityCompleteBtn')?.disabled === false);
    const originalTarget = await page.locator('#clipTargetSlider').evaluate(slider => ({
      value: Number(slider.value),
      max: Number(slider.max)
    }));
    assert.ok(Math.abs(originalTarget.value - originalTarget.max) < 0.11);
    assert.equal(await page.locator('#clipCodecSelect').isDisabled(), false);
    assert.match(await page.locator('#clipTargetHint').innerText(), /^100% · Original quality$/);
    assert.deepEqual(
      await page.evaluate(() => [
        normalizeSourceCodec('h.264'),
        normalizeSourceCodec('H.265'),
        normalizeSourceCodec('av01')
      ]),
      ['h264', 'h265', 'av1']
    );
    await page.evaluate(() => {
      clipState.qualityEditor.sourceBytes = 10 * 1048576;
      const slider = document.getElementById('clipTargetSlider');
      slider.max = '10';
      slider.value = '10';
      slider.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.locator('#clipCodecSelect').selectOption('h265');
    await page.waitForFunction(() => clipState?.qualityEditor?.mode === 'compressed');
    assert.match(await page.locator('#clipTargetHint').innerText(), /^100% · H\.265$/);
    await page.locator('#clipCodecSelect').selectOption('h264');
    await page.waitForFunction(() => clipState?.qualityEditor?.mode === 'original');
    await page.evaluate(() => {
      const slider = document.getElementById('clipTargetSlider');
      slider.value = '3';
      slider.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.waitForFunction(() => document.querySelector('#clipCodecSelect')?.disabled === false);
    assert.doesNotMatch(await page.locator('#clipQualityOverlay').innerText(), /[\u3400-\u9fff]/);
    await page.locator('#clipTargetSlider').evaluate(slider => {
      slider.value = slider.max;
      slider.dispatchEvent(new Event('input', { bubbles: true }));
    });
    assert.equal(await page.locator('#clipCodecSelect').isDisabled(), false);
    assert.equal(await page.evaluate(() => clipState?.qualityEditor?.mode), 'original');
    await page.evaluate(() => {
      clipState.downloadUrl = '/api/clips/stale.mp4';
      text('clipExportSubmitText', '重新下载');
    });
    await page.locator('#clipQualityCompleteBtn').click();
    assert.equal(await page.locator('#clipQualityOverlay').isVisible(), false);
    assert.equal(await page.locator('#clipOverlay').evaluate(element => element.inert), false);
    assert.equal(await page.evaluate(() => clipState.downloadUrl), '');
    assert.equal(await page.locator('#clipExportSubmitText').innerText(), 'Create and download');
    assert.equal(await page.locator('#clipQualityButton').innerText(), 'Configure quality and encoding');
    await page.locator('#clipCloseBtn').click();

    await search.fill('AUTO_WEB_NO_MATCH_EN');
    await search.press('Enter');
    await page.waitForFunction(() => document.querySelector('#resultsInfo')?.textContent?.trim()
      === 'No matching recordings');
    assert.doesNotMatch(
      await page.locator('#videoList').innerText(),
      /[\u3400-\u9fff]/);
    await search.fill('');
    await search.press('Enter');
    await page.waitForFunction(() => /^Page \d+ of \d+$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));

    await page.goto(`${baseUrl.replace(/\/$/, '')}/kuaidizs-install-guide`, { waitUntil: 'networkidle' });
    const guideText = await page.locator('.steps').innerText();
    assert.doesNotMatch(guideText, /[\u3400-\u9fff]/);

    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await page.getByRole('button', { name: 'Display language' }).click();
    const simplifiedChinese = page.getByRole('menuitemradio', { name: '简体中文' });
    await assert.doesNotReject(() => simplifiedChinese.waitFor());
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'networkidle' }),
      simplifiedChinese.click()
    ]);
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'PackingProof 包裹留证' }).waitFor());
    assert.equal(await page.evaluate(() => localStorage.getItem('expressWebLanguage')), 'zh-Hans');
    assert.deepEqual(pageErrors, [], '页面不应有未捕获的脚本异常');
  } finally {
    await browser.close();
  }
});

// 日语界面必须真的把录像列表加载出来（不能只翻文案）：列表停在加载中就是网页脚本初始化失败。
test('Web UI loads the recording list in Japanese', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'ja-JP' });
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(String(error)));
    await page.addInitScript(() => {
      if (!localStorage.getItem('expressWebLanguage')) localStorage.setItem('expressWebLanguage', 'ja-JP');
    });
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'PackingProof 荷物の証跡' }).waitFor());
    assert.equal(await page.locator('html').getAttribute('lang'), 'ja');
    assert.equal(await page.title(), 'PackingProof 荷物の証跡');
    await assertHeaderTitleNotTruncated(page);
    await page.setViewportSize({ width: 1000, height: 900 });
    await assertHeaderTitleNotTruncated(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.locator('#desktopAppDownloadButton').click();
    await assert.doesNotReject(() => page.locator('#desktopAppDownloadPopover.open').waitFor());
    await assertDownloadActionsWithinCards(page);
    await page.keyboard.press('Escape');

    const search = page.getByPlaceholder('注文番号またはファイル名で検索');
    await search.fill('AUTO_WEB_001');
    const searchResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/videos'
        && url.searchParams.get('keyword') === 'AUTO_WEB_001'
        && response.ok();
    });
    await search.press('Enter');
    await searchResponse;
    await page.waitForFunction(() => /^\d+ ページ中 \d+ ページ$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));

    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());
    await article.hover();
    await assert.doesNotReject(() => page.locator('#videoTip').waitFor({ state: 'visible' }));
    assert.doesNotMatch(
      (await page.locator('#videoTip .video-tip-label').allInnerTexts()).join(' '),
      /货|删|录/);
    await assert.doesNotReject(() => article.getByRole('button', { name: '再生', exact: true }).waitFor());
    await assert.doesNotReject(() => article.getByRole('button', { name: 'ダウンロード' }).waitFor());
    assert.equal(await page.locator('#filterButton').getAttribute('title'), '絞り込み');
    // 简体中文独有的字（货/删/录）不应残留在日语界面里
    assert.doesNotMatch(await page.locator('.toolbar').innerText(), /货|删|录/);
    assert.deepEqual(pageErrors, [], '页面不应有未捕获的脚本异常');
  } finally {
    await browser.close();
  }
});
