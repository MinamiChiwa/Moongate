import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const require = createRequire(import.meta.url);
const root = fileURLToPath(new URL('../../../', import.meta.url));
const { MabiToolClient } = require(resolve(root, 'artifacts/generated/sdk/typescript/compiled/client.js'));
const openapi = JSON.parse(readFileSync(resolve(root, 'artifacts/generated/contracts/openapi/v1.json'), 'utf8'));
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
ajv.addSchema(openapi, 'urn:mabitool:openapi');
const schema = name => ajv.compile({ $ref: `urn:mabitool:openapi#/components/schemas/${name}` });
const epoch = 'a'.repeat(32);
const time = '2026-11-10T00:00:00Z';
const chord = { key: 'K', control: true, alt: true, shift: false, windows: false };
const settings = { revision: '9007199254740993', actions: [{ actionId: 'emergency-stop', moduleId: 'core' }],
  bindings: [{ actionId: 'emergency-stop', chord }], registrations: [{ actionId: 'emergency-stop', configured: chord,
    registered: chord, status: 'registered', failureCode: null, usedFallback: false }] };
const ownership = { backendEpoch: epoch, simulation: true, observedAt: time, owners: [{ ownerId: 'color', operationId: 'color-run',
  gameSessionId: 'game-a', bindingRevision: '18446744073709551615', resources: ['foreground-input', 'activity-control:game-a'], expiresAt: time }] };
const receipt = { backendEpoch: epoch, simulation: true, stoppedAt: time, revokedLeases: 1, releasedControls: 1,
  inputReleaseState: 'released', cancellationRequested: true, effectStatus: 'unknown', failures: [] };

test('frontend shortcut notification schema and SDK retain identity, actual key and expiry', async () => {
  const eventSchema = JSON.parse(readFileSync(resolve(root, 'artifacts/generated/contracts/events/shortcut.triggered.v1.schema.json'), 'utf8'));
  const validate = ajv.compile(eventSchema);
  const envelope = { eventId: `${epoch}:42`, type: 'shortcut.triggered', schemaVersion: 1, module: 'presentation',
    resourceId: 'overlay-toggle', resourceRevision: '42', occurredAt: time, correlationId: null,
    data: { actionId: 'overlay-toggle', chord: { ...chord, key: 'F8', control: false, alt: false },
      triggeredAt: time, expiresAt: '2026-11-10T00:00:02Z' } };
  assert.equal(eventSchema['x-runtime-support'], 'implemented');
  assert.equal(validate(envelope), true, JSON.stringify(validate.errors));
  const { expiresAt, ...incomplete } = envelope.data;
  assert.equal(validate({ ...envelope, data: incomplete }), false);
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'fixture-token', async (url, options) => {
    assert.equal(options.headers['X-Event-Topics'], 'shortcut.triggered');
    return new Response(`event: shortcut.triggered\nid: cursor-42\ndata: ${JSON.stringify(envelope)}\n\n`,
      { headers: { 'Content-Type': 'text/event-stream' } });
  });
  const messages = [];
  for await (const message of sdk.streamEvents({ topics: ['shortcut.triggered'], resourceIds: [], lastEventId: null })) messages.push(message);
  assert.equal(messages.length, 1);
  assert.deepEqual(messages[0].data, envelope);
});

test('control schemas preserve input ownership, release evidence and actual shortcut registration', () => {
  for (const [name, value] of [['GameInputOwnershipSnapshot', ownership], ['GameEmergencyStopReceipt', receipt], ['ShortcutSettingsSnapshot', settings]]) {
    const validate = schema(name);
    assert.equal(validate(value), true, JSON.stringify(validate.errors));
  }
  const owners = schema('GameInputOwnershipSnapshot');
  assert.equal(owners({ ...ownership, owners: [{ ...ownership.owners[0], bindingRevision: 1 }] }), false);
  const { backendEpoch: omittedEpoch, ...missingEpoch } = ownership;
  assert.equal(owners(missingEpoch), false);
  const failed = { ...receipt, releasedControls: null, inputReleaseState: 'failed', failures: [{ component: 'input', code: 'input.releaseFailed' }] };
  assert.equal(schema('GameEmergencyStopReceipt')(failed), true);
});

test('generated SDK sends authenticated control and conditional shortcut requests', async () => {
  const calls = [];
  const transport = async (url, options) => {
    const path = new URL(url).pathname;
    calls.push({ path, options });
    const value = path.endsWith('input-ownership') ? ownership : path.endsWith('emergency-stop') ? receipt : settings;
    return new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json', ETag: '"shortcut-settings-9007199254740993"' } });
  };
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'fixture-token', transport);
  assert.equal((await sdk.getGameInputOwnership()).owners[0].ownerId, 'color');
  const request = { backendEpoch: epoch, expiresAt: '2026-11-10T00:00:30Z' };
  assert.equal((await sdk.emergencyStopGame(request, 'stop-request-a')).effectStatus, 'unknown');
  assert.equal((await sdk.getShortcutSettings()).revision, settings.revision);
  const tag = '"shortcut-settings-9007199254740993"';
  await sdk.changeShortcutSettings({ bindings: settings.bindings }, 'shortcut-request-a', tag);
  assert.deepEqual(calls.map(value => value.path), ['/api/v1/game/input-ownership', '/api/v1/game/emergency-stop',
    '/api/v1/system/shortcut-settings', '/api/v1/system/shortcut-settings']);
  assert.equal(calls[1].options.method, 'POST');
  assert.deepEqual(JSON.parse(calls[1].options.body), request);
  assert.equal(new Headers(calls[1].options.headers).get('Idempotency-Key'), 'stop-request-a');
  assert.equal(new Headers(calls[3].options.headers).get('If-Match'), tag);
  assert.equal(new Headers(calls[3].options.headers).get('Idempotency-Key'), 'shortcut-request-a');
  for (const value of calls) assert.equal(new Headers(value.options.headers).get('Authorization'), 'Bearer fixture-token');
});

test('module risk schema carries each declared high risk behavior and low risk modules', () => {
  const validate = schema('ModuleRisk');
  assert.equal(validate({ level: 'low', highRiskBehaviors: [] }), true);
  for (const behavior of ['memory-read', 'memory-injection', 'game-file-modification', 'game-packet-send'])
    assert.equal(validate({ level: 'high', highRiskBehaviors: [behavior] }), true);
  assert.equal(validate({ level: true, highRiskBehaviors: [] }), false);
});
