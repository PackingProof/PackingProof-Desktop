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

    const appDownloadButton = page.getByRole('button', { name: '下载 App' });
    await appDownloadButton.click();
    await assert.doesNotReject(() => page.locator('#desktopAppDownloadPopover.open').waitFor());
    await assert.doesNotReject(() => page.locator('#desktopAppDownloadQr[src^="data:image/png;base64,"]').waitFor());
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
    assert.equal(await page.locator('#desktopAppDownloadQr').isVisible(), false);
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

    const mobilePage = await context.newPage();
    await mobilePage.setViewportSize({ width: 390, height: 844 });
    let mobileDownloadInfoRequests = 0;
    mobilePage.on('request', request => {
      if (new URL(request.url()).pathname === '/api/mobile-app-download') mobileDownloadInfoRequests++;
    });
    await mobilePage.goto(baseUrl, { waitUntil: 'networkidle' });
    assert.equal(await mobilePage.getByRole('link', { name: '下载手机 App' }).isVisible(), true);
    assert.equal(await mobilePage.locator('#desktopAppDownloadQr').isVisible(), false);
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

    await article.getByRole('button', { name: '播放' }).click();
    await assert.doesNotReject(() => page.locator('#playerOverlay.active').waitFor());
    await page.waitForFunction(() => /\/api\/videos\/\d+\/play/.test(
      document.querySelector('#videoPlayer')?.getAttribute('src') || ''));
    const source = await page.locator('#videoPlayer').getAttribute('src');
    assert.match(source ?? '', /\/api\/videos\/\d+\/play/);
    await page.keyboard.press('Escape');

    await article.getByRole('button', { name: '剪辑' }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
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
    await assert.doesNotReject(() => page.locator('#compatModeText').filter({ hasText: 'Non-H.264 videos are automatically transcoded to H.264' }).waitFor());

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
    await assert.doesNotReject(() => article.getByRole('button', { name: 'Play' }).waitFor());
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
