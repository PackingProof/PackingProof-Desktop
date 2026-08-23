// PackingProof Extension API v1 serial scale adapter example.
// Yingzhan-compatible frame: ST,NT,+32.1000kg\r\n (18 bytes including CRLF).
// Requires Node.js 20+ and `npm install serialport`.

import { randomUUID } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { SerialPort } from 'serialport';
import { PackingProofExtensionClient } from './extension-v1-minimal.js';

const baseUrl = process.env.PACKINGPROOF_BASE_URL || 'http://127.0.0.1:5280';
const serialPath = process.env.PACKINGPROOF_SCALE_PORT;
const statePath = process.env.PACKINGPROOF_SCALE_STATE ||
  join(homedir(), '.packingproof', 'serial-scale-extension.json');

if (!serialPath) {
  throw new Error('Set PACKINGPROOF_SCALE_PORT, for example COM3');
}

export function parseYingzhanFrame(frame) {
  const bytes = Buffer.isBuffer(frame) ? frame : Buffer.from(frame);
  if (bytes.length !== 18 || bytes[16] !== 0x0d || bytes[17] !== 0x0a) return null;

  const text = bytes.subarray(0, 16).toString('ascii');
  const status = text.slice(0, 2);
  const weightType = text.slice(3, 5);
  const rawWeight = text.slice(6, 14).trim();
  const unit = text.slice(14, 16).toLowerCase();
  if (text[2] !== ',' || text[5] !== ',' ||
      !['ST', 'US'].includes(status) ||
      !['NT', 'TR'].includes(weightType) ||
      !/^[+-]\d+(?:\.\d+)?$/.test(rawWeight) || unit !== 'kg') {
    return null;
  }

  const value = Number(rawWeight);
  if (!Number.isFinite(value) || value < 0) return null;
  return {
    stable: status === 'ST',
    weightType,
    value: rawWeight.replace(/^\+/, ''),
    numericValue: value,
    unit,
    capturedAt: new Date()
  };
}

export class YingzhanFrameBuffer {
  constructor(onFrame) {
    this.buffer = Buffer.alloc(0);
    this.onFrame = onFrame;
  }

  push(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 2) {
      const end = this.buffer.indexOf(Buffer.from([0x0d, 0x0a]));
      if (end < 0) break;
      const frame = this.buffer.subarray(0, end + 2);
      this.buffer = this.buffer.subarray(end + 2);
      const reading = parseYingzhanFrame(frame);
      if (reading) this.onFrame(reading);
    }
    if (this.buffer.length > 1024) this.buffer = this.buffer.subarray(-18);
  }
}

async function loadState() {
  try {
    return JSON.parse(await readFile(statePath, 'utf8'));
  } catch {
    return null;
  }
}

async function saveState(value) {
  await mkdir(dirname(statePath), { recursive: true });
  await writeFile(statePath, JSON.stringify(value, null, 2), { mode: 0o600 });
}

const saved = await loadState();
const identity = saved?.identity || {
  extensionInstanceId: `serial-scale-${randomUUID()}`,
  providerId: 'packingproof.example.serial-scale',
  displayName: '串口电子秤示例',
  version: '1.0',
  source: 'https://github.com/PackingProof/PackingProof-Desktop',
  requestedPermissions: ['scan-tasks.read', 'scan-results.write', 'recording-fields.write'],
  requestedCapabilities: ['measurement.capture']
};
const client = new PackingProofExtensionClient(baseUrl, identity, saved?.credentialState || null);
if (!client.credentialState) {
  const enrollment = await client.enroll();
  await saveState({ identity, credentialState: enrollment.credentialState });
}

let latestStableReading = null;
const parser = new YingzhanFrameBuffer(reading => {
  if (reading.stable && reading.numericValue > 0) latestStableReading = reading;
});
const port = new SerialPort({
  path: serialPath,
  baudRate: 9600,
  dataBits: 8,
  stopBits: 1,
  parity: 'none',
  autoOpen: true
});
port.on('data', chunk => parser.push(chunk));
port.on('error', error => console.error('Serial port error:', error.message));

async function waitForStableReading(delivery) {
  const occurredAt = Date.parse(delivery.occurredAt);
  const expiresAt = Date.parse(delivery.expiresAt);
  const softDeadline = Date.parse(delivery.softDeadline);
  const deadline = Math.min(
    Number.isFinite(softDeadline) ? softDeadline : expiresAt - 500,
    expiresAt - 500);
  while (Date.now() < deadline) {
    const capturedAt = latestStableReading?.capturedAt?.getTime() || 0;
    const freshForScan = capturedAt >= occurredAt - 1000 && Date.now() - capturedAt <= 3000;
    if (freshForScan) return latestStableReading;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  return null;
}

async function ensureOk(response, operation) {
  if (response.ok) return;
  const body = await response.text();
  throw new Error(`${operation} failed: HTTP ${response.status} ${body}`);
}

let lastSuccessfulActivityAt = null;
let lastDataCount = 0;
setInterval(() => {
  client.heartbeat(lastSuccessfulActivityAt, lastDataCount)
    .then(response => ensureOk(response, 'heartbeat'))
    .catch(error => console.error(error.message));
}, 15000).unref();

while (true) {
  try {
    const response = await client.nextTask(20);
    if (response.status === 204) continue;
    await ensureOk(response, 'next task');
    const delivery = await response.json();
    await ensureOk(await client.acknowledge(delivery), 'acknowledge');

    const reading = await waitForStableReading(delivery);
    const observedAt = new Date().toISOString();
    const result = {
      deliveryId: delivery.deliveryId,
      taskId: delivery.taskId,
      providerId: identity.providerId,
      resultId: `scale-result-${randomUUID()}`,
      revision: 1,
      status: reading ? 'completed' : 'timeout',
      observedAt,
      orders: [],
      measurements: reading ? [{
        measurementType: 'weight',
        value: reading.value,
        unit: reading.unit,
        stable: true,
        capturedAt: reading.capturedAt.toISOString()
      }] : []
    };
    await ensureOk(await client.submitResult(result), 'submit result');
    lastSuccessfulActivityAt = observedAt;
    lastDataCount = reading ? 1 : 0;
    console.log(`${delivery.trackingNumber}: ${reading ? `${reading.value} ${reading.unit}` : 'no stable reading'}`);
  } catch (error) {
    console.error(error.message);
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
}
