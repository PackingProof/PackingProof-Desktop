import assert from 'node:assert/strict';
import test from 'node:test';
import { chromium } from 'playwright-core';

const baseUrl = process.env.EPM_AUTOMATION_BASE_URL;

test('isolated Web server supports search, playback and clip editor entry', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const context = await browser.newContext({ locale: 'zh-CN' });
    const page = await context.newPage();
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: '快递打包录像回放' }).waitFor());
    await page.waitForFunction(() => /^第 \d+ \/ \d+ 页$/.test(
      document.querySelector('#resultsInfo')?.textContent?.trim() || ''));

    const appDownloadButton = page.getByRole('button', { name: '下载 苹果/安卓版' });
    await appDownloadButton.click();
    await assert.doesNotReject(() => page.locator('#desktopAppDownloadPopover.open').waitFor());
    await assert.doesNotReject(() => page.locator('#desktopAndroidDownloadQr[src^="data:image/png;base64,"]').waitFor());
    await assert.doesNotReject(() => page.locator('#desktopIosDownloadQr[src^="data:image/png;base64,"]').waitFor());
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
    await assert.doesNotReject(() => page.locator('#mobileConnectQr[src^="data:image/png;base64,"]').waitFor());
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
    assert.equal(await page.locator('#pagination .page-btn').count(), 7);
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

    const search = page.getByPlaceholder('输入订单号关键词搜索');
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

    await article.getByRole('button', { name: '播放', exact: true }).click();
    await assert.doesNotReject(() => page.locator('#playerOverlay.active').waitFor());
    await page.waitForFunction(() => /\/api\/videos\/\d+\/play/.test(
      document.querySelector('#videoPlayer')?.getAttribute('src') || ''));
    const source = await page.locator('#videoPlayer').getAttribute('src');
    assert.match(source ?? '', /\/api\/videos\/\d+\/play/);
    await page.waitForFunction(() => document.querySelector('#playerTitle')?.textContent?.includes(
      'H.264 · 直接播放原片'));
    await page.keyboard.press('Escape');

    await article.getByRole('button', { name: '剪辑' }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
    await page.waitForFunction(() => typeof clipState !== 'undefined' && clipState?.sourceReady === true);
    await page.setViewportSize({ width: 1760, height: 1272 });
    await page.evaluate(() => applyClipSourceLayout(1920, 1080));
    const desktopClipGeometry = await page.locator('#clipOverlay').evaluate(overlay => {
      const dialog = overlay.querySelector('.clip-dialog').getBoundingClientRect();
      const body = overlay.querySelector('#clipEditorBody');
      const playButton = overlay.querySelector('#clipPlaySelectionBtn').getBoundingClientRect();
      const playIcon = overlay.querySelector('#clipPlaySelectionBtn .icon').getBoundingClientRect();
      const submitButton = overlay.querySelector('#clipSubmitBtn').getBoundingClientRect();
      const submitIcon = overlay.querySelector('#clipSubmitBtn .icon').getBoundingClientRect();
      return {
        dialogHeight: dialog.height,
        bodyClientHeight: body.clientHeight,
        bodyScrollHeight: body.scrollHeight,
        playIconWidth: playIcon.width,
        submitIconWidth: submitIcon.width,
        playCenterDelta: Math.abs((playButton.top + playButton.height / 2) - (playIcon.top + playIcon.height / 2)),
        submitCenterDelta: Math.abs((submitButton.top + submitButton.height / 2) - (submitIcon.top + submitIcon.height / 2))
      };
    });
    assert.ok(desktopClipGeometry.dialogHeight > 820);
    assert.ok(desktopClipGeometry.bodyScrollHeight <= desktopClipGeometry.bodyClientHeight + 1);
    assert.ok(desktopClipGeometry.playIconWidth >= 19);
    assert.ok(desktopClipGeometry.submitIconWidth >= 19);
    assert.ok(desktopClipGeometry.playCenterDelta <= 1);
    assert.ok(desktopClipGeometry.submitCenterDelta <= 1);
    await page.evaluate(() => applyClipSourceLayout(1080, 1920));
    assert.equal(await page.locator('#clipMainPreview').getAttribute('data-orientation'), 'portrait');
    assert.equal(await page.locator('body').evaluate(element => element.classList.contains('clip-open')), true);
    await page.setViewportSize({ width: 390, height: 844 });
    const portraitClipGeometry = await page.locator('#clipOverlay').evaluate(overlay => {
      const preview = overlay.querySelector('#clipMainPreview').getBoundingClientRect();
      const actions = overlay.querySelector('.clip-actions').getBoundingClientRect();
      const submit = overlay.querySelector('#clipSubmitBtn').getBoundingClientRect();
      return {
        previewHeight: preview.height,
        previewWidth: preview.width,
        actionsTop: actions.top,
        actionsBottom: actions.bottom,
        submitTop: submit.top,
        submitBottom: submit.bottom,
        viewportHeight: window.innerHeight
      };
    });
    assert.ok(portraitClipGeometry.previewHeight > 300);
    assert.ok(portraitClipGeometry.previewWidth <= 620);
    assert.ok(portraitClipGeometry.actionsTop >= 0);
    assert.ok(portraitClipGeometry.actionsBottom <= portraitClipGeometry.viewportHeight);
    assert.ok(portraitClipGeometry.submitTop >= 0);
    assert.ok(portraitClipGeometry.submitBottom <= portraitClipGeometry.viewportHeight);
    const compactInfo = await page.locator('#clipEditorBody').evaluate(body => ({
      infoColumns: getComputedStyle(body.querySelector('.clip-info')).gridTemplateColumns.split(' ').length,
      rangeDisplay: getComputedStyle(body.querySelector('.clip-summary-item')).display,
      durationDisplay: getComputedStyle(body.querySelector('.clip-summary-item.duration')).display
    }));
    assert.deepEqual(compactInfo, {
      infoColumns: 3,
      rangeDisplay: 'none',
      durationDisplay: 'flex'
    });
    await page.locator('#clipCloseBtn').click();
    assert.equal(await page.locator('body').evaluate(element => element.classList.contains('clip-open')), false);
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
    await page.addInitScript(() => {
      if (!localStorage.getItem('expressWebLanguage')) localStorage.setItem('expressWebLanguage', 'en-US');
    });
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: 'Packing Monitor Recordings' }).waitFor());
    assert.equal(await page.locator('html').getAttribute('lang'), 'en');
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
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Play', exact: true }).waitFor());
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Download' }).waitFor());
    assert.doesNotMatch(await article.innerText(), /发货|退货|文件存在|文件丢失|播放|下载/);
    assert.equal(await page.locator('#startDate').getAttribute('type'), 'text');
    assert.equal(await page.locator('#startDate').getAttribute('placeholder'), 'YYYY-MM-DD');

    await page.evaluate(() => renderPagination(2, 3));
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Previous' }).waitFor());
    await assert.doesNotReject(() => page.getByRole('button', { name: 'Next' }).waitFor());

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
    await assert.doesNotReject(() => page.getByRole('heading', { name: '快递打包录像回放' }).waitFor());
    assert.equal(await page.evaluate(() => localStorage.getItem('expressWebLanguage')), 'zh-Hans');
  } finally {
    await browser.close();
  }
});
