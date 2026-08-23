// PackingProof Extension API v1 minimal JavaScript client.
// This file is an example library: it does not run, store credentials, or contact a host by itself.

const encoder = new TextEncoder();

function bytesToHex(bytes) {
  return Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
}

function hexToBytes(value) {
  const normalized = String(value || '').trim();
  if (normalized.length % 2 !== 0 || !/^[a-f0-9]+$/i.test(normalized)) {
    throw new Error('Invalid hexadecimal credential');
  }
  return new Uint8Array(normalized.match(/../g).map(part => Number.parseInt(part, 16)));
}

function randomHex(byteLength) {
  const bytes = new Uint8Array(byteLength);
  globalThis.crypto.getRandomValues(bytes);
  return bytesToHex(bytes);
}

async function sha256Hex(bytes) {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return bytesToHex(new Uint8Array(digest));
}

async function hmacHex(credential, canonical) {
  const key = await globalThis.crypto.subtle.importKey(
    'raw',
    hexToBytes(credential),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign']);
  const signature = await globalThis.crypto.subtle.sign('HMAC', key, encoder.encode(canonical));
  return bytesToHex(new Uint8Array(signature));
}

export class PackingProofExtensionClient {
  constructor(baseUrl, identity, credentialState = null) {
    this.baseUrl = String(baseUrl || '').replace(/\/$/, '');
    this.identity = identity;
    this.credentialState = credentialState;
  }

  async getCapabilities() {
    const response = await fetch(`${this.baseUrl}/api/extensions/v1/capabilities`);
    return response.json();
  }

  async enroll() {
    const response = await fetch(`${this.baseUrl}/api/extensions/v1/enroll`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        requestId: `enroll-${randomHex(12)}`,
        requestSecret: randomHex(32),
        extensionInstanceId: this.identity.extensionInstanceId,
        providerId: this.identity.providerId,
        displayName: this.identity.displayName,
        version: this.identity.version,
        source: this.identity.source,
        requestedPermissions: this.identity.requestedPermissions,
        requestedCapabilities: this.identity.requestedCapabilities
      })
    });
    const payload = await response.json();
    if (!response.ok) throw new Error(payload.errorCode || `Enrollment HTTP ${response.status}`);

    this.credentialState = {
      extensionInstanceId: payload.extensionInstanceId,
      credential: payload.credential,
      credentialGeneration: payload.credentialGeneration
    };
    // Persist this object in the adapter's protected credential store. Never put it in source code.
    return { credentialState: this.credentialState, approval: payload };
  }

  async signedRequest(method, requestTarget, value = undefined) {
    if (!this.credentialState) throw new Error('Extension is not enrolled');
    const body = value === undefined ? new Uint8Array() : encoder.encode(JSON.stringify(value));
    const timestamp = Math.floor(Date.now() / 1000);
    const nonce = randomHex(16);
    const contentHash = await sha256Hex(body);
    const canonical = [
      'packingproof-extension-request-v1',
      '1',
      String(this.credentialState.credentialGeneration),
      method.toUpperCase(),
      requestTarget,
      String(timestamp),
      nonce,
      contentHash,
      this.credentialState.extensionInstanceId
    ].join('\n');
    const signature = await hmacHex(this.credentialState.credential, canonical);
    const headers = {
      'X-PackingProof-Extension-Version': '1',
      'X-PackingProof-Extension-Id': this.credentialState.extensionInstanceId,
      'X-PackingProof-Extension-Credential-Generation': String(this.credentialState.credentialGeneration),
      'X-PackingProof-Extension-Timestamp': String(timestamp),
      'X-PackingProof-Extension-Nonce': nonce,
      'X-PackingProof-Extension-Content-SHA256': contentHash,
      'X-PackingProof-Extension-Signature': signature
    };
    if (value !== undefined) headers['Content-Type'] = 'application/json';

    return fetch(`${this.baseUrl}${requestTarget}`, {
      method,
      headers,
      body: value === undefined ? undefined : body
    });
  }

  heartbeat(lastSuccessfulActivityAt = null, dataCount = 0) {
    return this.signedRequest('POST', '/api/extensions/v1/heartbeat', {
      version: this.identity.version,
      capabilities: this.identity.requestedCapabilities,
      lastSuccessfulActivityAt,
      dataCount
    });
  }

  nextTask(waitSeconds = 20) {
    return this.signedRequest(
      'GET',
      `/api/extensions/v1/scan-tasks/next?waitSeconds=${Math.max(0, Math.min(25, waitSeconds))}`);
  }

  acknowledge(delivery) {
    return this.signedRequest(
      'POST',
      `/api/extensions/v1/scan-tasks/${encodeURIComponent(delivery.deliveryId)}/ack`,
      { taskId: delivery.taskId });
  }

  submitResult(result) {
    return this.signedRequest('POST', '/api/extensions/v1/scan-results', result);
  }
}

// Example identity. Generate extensionInstanceId once and persist it; do not regenerate it per request.
export const exampleIdentity = {
  extensionInstanceId: 'example-0123456789abcdef0123456789abcdef',
  providerId: 'example.adapter',
  displayName: 'Example PackingProof Adapter',
  version: '1.0',
  source: 'https://example.com/packingproof-adapter',
  requestedPermissions: ['scan-tasks.read', 'scan-results.write'],
  requestedCapabilities: ['order.lookup', 'refund.lookup']
};
