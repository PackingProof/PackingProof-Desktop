import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import vm from 'node:vm';

const repoRoot = path.resolve(import.meta.dirname, '..', '..');
const scriptPath = path.join(repoRoot, 'Scripts', '快递助手订单推送.user.js');
const source = fs.readFileSync(scriptPath, 'utf8');

function between(startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, `Cannot extract ${startMarker}`);
  return source.slice(start, end);
}

function createLeaseWorker(store, token, closed) {
  const context = {
    REFUND_WORKER_HEARTBEAT_KEY: 'heartbeat',
    REFUND_WORKER_TOKEN: token,
    REFUND_WORKER_STALE_MS: 10 * 60 * 1000,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    delay: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    document: { title: '' },
    window: { close: () => closed.push(token) },
    location: { replace: () => {} },
    setTimeout,
    Math,
    Date
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getRefundWorkerHeartbeat()', '    function getOrderLookupPendingUrl()') +
      ';globalThis.claimLease=claimRefundWorkerLease;',
    context);
  return context;
}

function createWorkerManager({ reachable, savedTabs = {} }) {
  const store = new Map();
  const opened = [];
  const currentTab = {};
  const context = {
    REFUND_WORKER_HEARTBEAT_KEY: 'heartbeat',
    REFUND_WORKER_OPEN_LOCK_KEY: 'open-lock',
    REFUND_WORKER_TOKEN: 'ordinary-page',
    REFUND_WORKER_STALE_MS: 10 * 60 * 1000,
    REFUND_WORKER_OPEN_COOLDOWN_MS: 10 * 60 * 1000,
    IS_REFUND_WORKER: false,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    GM_getTab: callback => callback(currentTab),
    GM_saveTab: (tab, callback) => callback?.(),
    GM_getTabs: callback => callback(savedTabs),
    GM_openInTab: url => opened.push(url),
    canConnectHost: async () => reachable,
    buildRefundWorkerUrl: () => 'https://p4.kuaidizs.cn/?epm_refund_worker=1',
    delay: () => Promise.resolve(),
    document: { querySelector: () => ({}) },
    window: { close() {}, addEventListener() {} },
    location: { replace() {} },
    setTimeout,
    Math,
    Date,
    Object,
    Promise
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getRefundWorkerHeartbeat()', '    function getOrderLookupPendingUrl()') +
      ';globalThis.ensureWorker=ensureRefundWorker;globalThis.maintainWorker=maintainRefundWorker;',
    context);
  return { context, opened };
}

test('concurrent refund workers keep exactly one lease owner', async () => {
  const store = new Map();
  const closed = [];
  const workers = Array.from({ length: 8 }, (_, index) => createLeaseWorker(store, `worker-${index}`, closed));
  const results = await Promise.all(workers.map(worker => worker.claimLease()));
  assert.equal(results.filter(Boolean).length, 1);
  assert.equal(closed.length, 7);
});

test('fresh lease is retained and stale lease can recover', async () => {
  const store = new Map([['heartbeat', { token: 'owner', time: Date.now() }]]);
  const closed = [];
  assert.equal(await createLeaseWorker(store, 'new-worker', closed).claimLease(), false);
  assert.deepEqual(store.get('heartbeat').token, 'owner');

  store.set('heartbeat', { token: 'stale-owner', time: Date.now() - 10 * 60 * 1000 - 1 });
  assert.equal(await createLeaseWorker(store, 'replacement', closed).claimLease(), true);
  assert.equal(store.get('heartbeat').token, 'replacement');
});

test('offline monitor never opens a refund worker', async () => {
  const { context, opened } = createWorkerManager({ reachable: false });
  await context.maintainWorker();
  assert.equal(opened.length, 0);
});

test('saved refund worker tab prevents duplicate even with stale heartbeat', async () => {
  const { context, opened } = createWorkerManager({
    reachable: true,
    savedTabs: { existing: { epmRefundWorker: true } }
  });
  await context.ensureWorker(false, true);
  assert.equal(opened.length, 0);
});

function createLatestOrderContext() {
  const selects = {
    '#orderShowSortDim': {
      value: '0',
      events: [],
      dispatchEvent(event) { this.events.push(event.type); }
    },
    '#orderShowSortType': {
      value: '10',
      events: [],
      dispatchEvent(event) { this.events.push(event.type); }
    }
  };
  const context = {
    document: { querySelector: selector => selects[selector] || null },
    delay: () => Promise.resolve(),
    Event: class Event {
      constructor(type) { this.type = type; }
    },
    Array,
    Set,
    Map,
    String,
    Promise,
    Error
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function selectLatestOrdersByTrackingNumber(', '    async function queryOrdersByTrackingNumbers(') +
      ';globalThis.selectLatest=selectLatestOrdersByTrackingNumber;globalThis.ensureNewestSort=ensureNewestOrderFirstSort;',
    context);
  return { context, selects };
}

test('reused tracking number keeps the first newest order even when an older order was refunded', () => {
  const { context } = createLatestOrderContext();
  const selected = context.selectLatest([
    { trackingNumber: 'TRACK-1', orderId: 'NEW', isPrintedRefund: false, refundStatus: 'NO_REFUND' },
    { trackingNumber: 'TRACK-1', orderId: 'OLD', isPrintedRefund: true, refundStatus: 'SUCCESS' }
  ], ['track-1']);

  assert.equal(selected.length, 1);
  assert.equal(selected[0].orderId, 'NEW');
  assert.equal(selected[0].isPrintedRefund, false);
});

test('latest refunded order wins over an older normal order', () => {
  const { context } = createLatestOrderContext();
  const selected = context.selectLatest([
    { trackingNumber: 'TRACK-1', orderId: 'NEW', isPrintedRefund: true, refundStatus: 'SUCCESS' },
    { trackingNumber: 'TRACK-1', orderId: 'OLD', isPrintedRefund: false, refundStatus: 'NO_REFUND' }
  ], ['TRACK-1']);

  assert.equal(selected[0].orderId, 'NEW');
  assert.equal(selected[0].isPrintedRefund, true);
});

test('successful exact lookup emits a normal result when the tracking number is absent', () => {
  const { context } = createLatestOrderContext();
  const selected = context.selectLatest([], ['TRACK-1']);

  assert.equal(selected[0].trackingNumber, 'TRACK-1');
  assert.equal(selected[0].isPrintedRefund, false);
  assert.equal(selected[0].refundStatus, 'NO_REFUND');
});

test('refund worker forces unified newest-order-first sorting', async () => {
  const { context, selects } = createLatestOrderContext();

  await context.ensureNewestSort();

  assert.equal(selects['#orderShowSortDim'].value, '1');
  assert.equal(selects['#orderShowSortType'].value, '21');
  assert.deepEqual(selects['#orderShowSortDim'].events, ['change']);
  assert.deepEqual(selects['#orderShowSortType'].events, ['change']);
});

test('requested refund lookup always uses exact tracking-number search', () => {
  const body = between('    async function queryRequestedRefundSnapshot(', '    let orderLookupPollStarted');
  assert.match(body, /return queryOrdersByTrackingNumbers\(requested\)/);
  assert.doesNotMatch(body, /missing = requested\.filter/);
});

test('userscript keeps installed recorders as the only order targets', () => {
  assert.match(source, /const PACKING_PROOF_RECORDERS = \[\];/);
  assert.match(source, /const PACKING_PROOF_HOST = null;/);
  assert.doesNotMatch(source, /findMonitorAddress|ensureMonitorAddress|monitor_auto_discovery/);
  assert.doesNotMatch(source, /切换上位机|添加上位机|移除上位机|重新连接上位机/);
});

test('order push broadcasts to every paired receiver and tolerates one offline device', async () => {
  const requests = [];
  const notifications = [];
  const devices = [
    { nodeId: 'pc', name: 'PC recorder', type: 'pc', url: 'http://192.168.31.250:5280' },
    { nodeId: 'phone', name: 'Phone recorder', type: 'mobile', url: 'http://192.168.31.205:5280' }
  ];
  const context = {
    DEFAULT_PORT: 5280,
    RECORDER_STATUS_TIMEOUT: 900,
    RECORDER_STATUS_CACHE_MS: 15000,
    ONLINE_RECORDER_TIMEOUT: 3500,
    OFFLINE_RECORDER_TIMEOUT: 1800,
    UNKNOWN_RECORDER_TIMEOUT: 3000,
    normalizeAddress: value => {
      if (/^https?:\/\//i.test(String(value))) {
        const url = new URL(String(value));
        return { host: url.hostname, port: Number(url.port || 5280) };
      }
      const [host, port = '5280'] = String(value).split(':');
      return { host, port: Number(port) };
    },
    formatAddress: address => `${address.host}:${address.port}`,
    getBaseUrl: (host, port) => `http://${host}:${port}`,
    getHostBaseUrl: () => 'http://192.168.31.250:5280',
    requestMonitor: async () => ({ status: 404, body: {} }),
    getRecorderDevices: () => devices,
    gmGet: async (url, timeout) => {
      assert.equal(url, 'http://192.168.31.250:5280/api/recording-devices');
      assert.equal(timeout, 900);
      return {
        ok: true,
        response: {
          devices: [{
            nodeId: 'pc',
            address: 'http://192.168.31.250:5280',
            online: true
          }]
        }
      };
    },
    parseJsonResponse: text => JSON.parse(text || '{}'),
    GM_xmlhttpRequest: options => {
      requests.push({ url: options.url, timeout: options.timeout });
      queueMicrotask(() => {
        if (options.url.includes('192.168.31.250'))
          options.onload({ status: 200, responseText: '{"ok":true,"testCount":1}' });
        else
          options.onerror(new Error('offline'));
      });
    },
    showNotification: message => notifications.push(message),
    debugLog: () => {},
    console: { info() {}, warn() {} },
    Promise,
    Number,
    String,
    JSON,
    URL
  };
  vm.createContext(context);
  vm.runInContext(
    between('    let recorderStatusCache', '    function getHostAddress()') +
    between('    function sendOrderToRecorder(', '    function requestMonitor(') +
      ';globalThis.pushOrders=pushToMonitor;',
    context);

  const result = await context.pushOrders([{ trackingNumber: 'TRACK-1' }], { isTest: true });
  assert.equal(requests.length, 2);
  assert.match(requests[0].url, /192\.168\.31\.250/);
  assert.equal(requests[0].timeout, 3500);
  assert.match(requests[1].url, /192\.168\.31\.205/);
  assert.equal(requests[1].timeout, 1800);
  assert.equal(result.ok, true);
  assert.equal(result.confirmed, true);
  assert.equal(result.successfulCount, 1);
  assert.equal(result.targetCount, 2);
  assert.match(notifications[0], /1\/2/);
});

test('order push still broadcasts to every recorder when host status lookup fails', async () => {
  const requests = [];
  const devices = [
    { nodeId: 'pc', name: 'PC recorder', type: 'pc', url: 'http://192.168.31.250:5280' },
    { nodeId: 'phone', name: 'Phone recorder', type: 'mobile', url: 'http://192.168.31.205:5280' }
  ];
  const context = {
    DEFAULT_PORT: 5280,
    RECORDER_STATUS_TIMEOUT: 900,
    RECORDER_STATUS_CACHE_MS: 15000,
    ONLINE_RECORDER_TIMEOUT: 3500,
    OFFLINE_RECORDER_TIMEOUT: 1800,
    UNKNOWN_RECORDER_TIMEOUT: 3000,
    normalizeAddress: value => {
      const url = new URL(String(value));
      return { host: url.hostname, port: Number(url.port || 5280) };
    },
    formatAddress: address => `${address.host}:${address.port}`,
    getBaseUrl: (host, port) => `http://${host}:${port}`,
    getHostBaseUrl: () => 'http://192.168.31.250:5280',
    requestMonitor: async () => ({ status: 404, body: {} }),
    getRecorderDevices: () => devices,
    gmGet: async () => ({ ok: false, response: {} }),
    parseJsonResponse: text => JSON.parse(text || '{}'),
    GM_xmlhttpRequest: options => {
      requests.push({ url: options.url, timeout: options.timeout });
      queueMicrotask(() => options.onload({ status: 200, responseText: '{"ok":true}' }));
    },
    showNotification() {},
    console: { info() {}, warn() {} },
    Promise,
    Number,
    String,
    JSON,
    URL,
    Date,
    Set
  };
  vm.createContext(context);
  vm.runInContext(
    between('    let recorderStatusCache', '    function getHostAddress()') +
      between('    function sendOrderToRecorder(', '    function requestMonitor(') +
      ';globalThis.pushOrders=pushToMonitor;',
    context);

  const result = await context.pushOrders([{ trackingNumber: 'TRACK-1' }], {});

  assert.equal(result.successfulCount, 2);
  assert.equal(requests.length, 2);
  assert.deepEqual(requests.map(request => request.timeout), [3000, 3000]);
});

test('order push uses host relay addresses after a recorder IP change', async () => {
  const directRequests = [];
  const relayRequests = [];
  const context = {
    DEFAULT_PORT: 5280,
    RECORDER_STATUS_TIMEOUT: 900,
    RECORDER_STATUS_CACHE_MS: 15000,
    ONLINE_RECORDER_TIMEOUT: 3500,
    OFFLINE_RECORDER_TIMEOUT: 1800,
    UNKNOWN_RECORDER_TIMEOUT: 3000,
    getHostBaseUrl: () => 'http://192.168.31.250:5280',
    requestMonitor: async (method, url, data, timeout) => {
      relayRequests.push({ method, url, data, timeout });
      return {
        status: 200,
        body: {
          results: [{
            nodeId: 'recorder-node',
            name: '录像工位',
            type: 'pc',
            address: 'http://192.168.31.88:5280',
            ok: true,
            status: 200,
            testCount: 1
          }]
        }
      };
    },
    getRecorderDevices: () => [
      {
        nodeId: 'recorder-node',
        name: '录像工位',
        type: 'pc',
        url: 'http://192.168.31.20:5280'
      },
      {
        nodeId: 'offline-node',
        name: '离线工位',
        type: 'pc',
        url: 'http://192.168.31.21:5280'
      }
    ],
    GM_xmlhttpRequest: options => directRequests.push(options.url),
    showNotification() {},
    console: { info() {}, warn() {} },
    Promise,
    Number,
    String,
    JSON,
    Array
  };
  vm.createContext(context);
  vm.runInContext(
    between('    let recorderStatusCache', '    function getHostAddress()') +
      between('    function sendOrderToRecorder(', '    function requestMonitor(') +
      ';globalThis.pushOrders=pushToMonitor;',
    context);

  const result = await context.pushOrders([{ trackingNumber: 'TRACK-1' }], { isTest: true });

  assert.equal(relayRequests.length, 1);
  assert.equal(relayRequests[0].url, 'http://192.168.31.250:5280/api/orderinfo/broadcast');
  assert.deepEqual(relayRequests[0].data.targetNodeIds, ['recorder-node', 'offline-node']);
  assert.equal(directRequests.length, 0);
  assert.equal(result.successfulCount, 1);
  assert.equal(result.targetCount, 2);
  assert.equal(result.results[0].address, 'http://192.168.31.88:5280');
  assert.equal(result.confirmed, true);
});

function createConnectionHeartbeatContext(status = 200) {
  const store = new Map();
  const requests = [];
  const intervals = [];
  let hostChecks = 0;
  const context = {
    CONNECTION_CLIENT_ID_KEY: 'connection_client_id',
    CONNECTION_HEARTBEAT_INTERVAL_MS: 15000,
    GM_getValue: (key, fallback) => store.has(key) ? store.get(key) : fallback,
    GM_setValue: (key, value) => store.set(key, value),
    getHostBaseUrl: () => 'http://192.168.1.20:5280',
    getConnectionHeartbeatUrl: () => 'http://192.168.1.20:5280/api/connections/heartbeat',
    requestMonitor: async (method, url, data, timeout) => {
      requests.push({ method, url, data, timeout });
      return { status, body: {} };
    },
    canConnectHost: async () => { hostChecks += 1; return status === 200; },
    setInterval: (callback, delay) => { intervals.push({ callback, delay }); return intervals.length; },
    Math,
    Date,
    String,
    Promise
  };
  vm.createContext(context);
  vm.runInContext(
    between('    function getConnectionClientId()', '    function delay(') +
      ';globalThis.getClientId=getConnectionClientId;globalThis.sendHeartbeat=sendConnectionHeartbeat;globalThis.startHeartbeat=startConnectionHeartbeat;',
    context);
  return { context, store, requests, intervals, getHostChecks: () => hostChecks };
}

test('userscript heartbeat keeps one persistent id across tabs', async () => {
  const first = createConnectionHeartbeatContext();
  const id = first.context.getClientId();
  assert.equal(first.context.getClientId(), id);
  assert.equal(first.store.get('connection_client_id'), id);
  assert.match(id, /^userscript-/);

  await first.context.sendHeartbeat();
  assert.equal(first.requests.length, 1);
  assert.equal(first.requests[0].data.clientId, id);
  assert.equal(first.requests[0].data.clientType, 'userscript');
});

test('userscript heartbeat uses 15 second interval and validates only the installed host', async () => {
  const failed = createConnectionHeartbeatContext(0);
  failed.context.startHeartbeat();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(failed.intervals.length, 1);
  assert.equal(failed.intervals[0].delay, 15000);
  assert.equal(failed.getHostChecks(), 1);
});
