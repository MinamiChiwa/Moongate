import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const snapshot = read('contracts/fixtures/inventory-snapshot.v1.json');
const ownedScores = read('contracts/fixtures/music-owned-score-page.v1.json');
const require = createRequire(import.meta.url);
const { MabiToolClient } = require(resolve(root, 'artifacts/generated/sdk/typescript/compiled/client.js'));
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
ajv.addSchema(read('artifacts/generated/contracts/openapi/v1.json'), 'urn:mabitool:inventory-api');
const publicSchema = name => ajv.compile({ $ref: 'urn:mabitool:inventory-api#/components/schemas/' + name });
const valid = (validate, value) => assert.equal(validate(value), true, JSON.stringify(validate.errors));

test('owned instrument contract preserves client identities, inventory source and unknown evidence', () => {
  const view = snapshot.data.items[2];
  const item = { type: view.name.type, container: view.item.container, key: view.item.key,
    instanceId: view.item.instanceId, quantity: view.item.quantity, equipped: view.item.equipped,
    name: view.name, clientInstrumentId: 'client:TW:Instrument:9007199254740997',
    instrumentTypeId: '9007199254740997', equipmentToolTypeId: '1001', mapping: 'client-catalog',
    current: view.current, quality: view.quality, lastObservedAt: view.lastObservedAt, source: view.source };
  const page = { backendEpoch: snapshot.backendEpoch, inventoryRevision: snapshot.resourceRevision,
    catalogRevision: snapshot.catalog.revision, catalogId: snapshot.catalog.id, gameSessionId: snapshot.data.gameSessionId,
    bindingRevision: snapshot.data.bindingRevision, generation: snapshot.data.generation, identity: snapshot.data.identity,
    source: snapshot.data.source, simulation: true, current: true, quality: snapshot.data.quality,
    coverage: snapshot.data.coverage, reasons: [], items: [item], nextCursor: null };
  const validate = publicSchema('MusicOwnedInstrumentPage');
  valid(validate, page);
  for (const field of ['instanceId', 'quantity', 'instrumentTypeId', 'equipmentToolTypeId'])
    assert.equal(validate({ ...page, items: [{ ...item, [field]: 9007199254740993 }] }), false);
  assert.equal(validate({ ...page, inventoryRevision: 1 }), false);
  valid(validate, { ...page, current: false, quality: 'future-quality', reasons: ['inventory.bindingChanged'],
    items: [{ ...item, quantity: null, clientInstrumentId: null, mapping: 'future-mapping', current: false }] });
  const query = publicSchema('MusicOwnedInstrumentQuery');
  valid(query, { container: 'backpack', pageSize: 1, cursor: null, currentOnly: true });
  assert.equal(query({ pageSize: 1, rawInput: true }), false);
  assert.equal(query({ container: 1 }), false);
});

test('owned instrument SDK preserves the read query and bearer authentication', async () => {
  const calls = [];
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'fixture-token', async (url, options) => {
    calls.push({ path: new URL(url).pathname, options });
    return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
  });
  const query = { container: 'character-storage', pageSize: 1, cursor: 'opaque-cursor', currentOnly: false };
  await sdk.queryOwnedMusicInstruments(query);
  assert.equal(calls[0].path, '/api/v1/music/instruments/owned/query');
  assert.equal(calls[0].options.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].options.body), query);
  assert.equal(new Headers(calls[0].options.headers).get('Authorization'), 'Bearer fixture-token');
  assert.equal(new Headers(calls[0].options.headers).has('Idempotency-Key'), false);
  assert.equal(new Headers(calls[0].options.headers).has('If-Match'), false);
});

test('shared owned score fixture preserves instances, raw metadata and incomplete evidence', () => {
  const validate = publicSchema('MusicOwnedScorePage');
  valid(validate, ownedScores);
  const first = ownedScores.items[0];
  const second = ownedScores.items[1];
  assert.equal(first.instanceId, '18446744073709551615');
  assert.equal(first.sheetId, '9007199254740993');
  assert.equal(first.acquiredTimeRaw, '-9223372036854775808');
  assert.equal(first.rawTitle, ' <color=#ffffff>同名譜</color> ');
  assert.equal(first.rawTitle, second.rawTitle);
  assert.notEqual(first.instanceId, second.instanceId);
  assert.notEqual(first.container, second.container);
  assert.equal(first.attributes.data.Title, first.rawTitle);
  assert.equal(first.composerRealmId, 3);
  assert.equal(second.quantity, null);
  assert.equal(second.metadataComplete, false);
  assert.equal(ownedScores.complete, false);
  assert.equal(ownedScores.coverage[2].complete, false);
  assert.equal(Object.hasOwn(ownedScores.identity, 'realmId'), false);
  const preserved = JSON.parse(JSON.stringify(ownedScores));
  assert.deepEqual(preserved, ownedScores);
  for (const field of ['instanceId', 'quantity', 'sheetId']) {
    for (const value of [9007199254740993, '18446744073709551616', '01'])
      assert.equal(validate({ ...ownedScores, items: [{ ...first, [field]: value }] }), false);
  }
  for (const field of ['inventoryRevision', 'workspaceRevision', 'bindingRevision', 'generation'])
    assert.equal(validate({ ...ownedScores, [field]: 1 }), false);
  assert.equal(validate({ ...ownedScores, items: [{ ...first, acquiredTimeRaw: -1 }] }), false);
  assert.equal(validate({ ...ownedScores, items: [{ ...first, isCopyingAllowed: 'true' }] }), false);
  valid(validate, { ...ownedScores, current: false, quality: 'future-quality',
    items: [{ ...second, container: 'future-container', quality: 'future-quality', source: null }] });
  const query = publicSchema('MusicOwnedScoreQuery');
  valid(query, { container: 'account-storage', pageSize: 1, cursor: null, currentOnly: true });
  assert.equal(query({ pageSize: 1, rawInput: true }), false);
  assert.equal(query({ container: 1 }), false);
});

test('owned score SDK returns metadata and carries authenticated read query cancellation', async () => {
  const calls = [];
  const controller = new AbortController();
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'fixture-token', async (url, options) => {
    calls.push({ path: new URL(url).pathname, options });
    return new Response(JSON.stringify(ownedScores), { status: 200, headers: { 'Content-Type': 'application/json' } });
  });
  const query = { container: 'character-storage', pageSize: 1, cursor: 'opaque-cursor', currentOnly: false };
  const page = await sdk.queryOwnedMusicScores(query, controller.signal);
  assert.deepEqual(page, ownedScores);
  assert.equal(calls[0].path, '/api/v1/music/scores/owned/query');
  assert.equal(calls[0].options.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].options.body), query);
  assert.equal(calls[0].options.signal, controller.signal);
  assert.equal(new Headers(calls[0].options.headers).get('Authorization'), 'Bearer fixture-token');
  assert.equal(new Headers(calls[0].options.headers).has('Idempotency-Key'), false);
  assert.equal(new Headers(calls[0].options.headers).has('If-Match'), false);
});

test('inventory snapshot preserves category identities, original names and optional UInt64 evidence', () => {
  const validate = publicSchema('InventorySnapshot');
  valid(validate, snapshot);
  assert.equal(new Set(snapshot.data.items.map(value => value.item.category + ':' + value.item.typeId)).size, 3);
  assert.equal(snapshot.data.items[2].name.isInstrument, true);
  assert.equal(snapshot.data.items[0].name.rawName, '<color=#ffffff>原始药水</color>');
  assert.equal(snapshot.data.coverage[3].complete, false);
  for (const changes of [{ gold: 9007199254740993 }, { gold: '18446744073709551616' }, { bindingRevision: 1 }])
    assert.equal(validate({ ...snapshot, data: { ...snapshot.data, ...changes } }), false);
  for (const field of ['typeId', 'instanceId', 'quantity']) {
    const items = structuredClone(snapshot.data.items); items[1].item[field] = 9007199254740993;
    assert.equal(validate({ ...snapshot, data: { ...snapshot.data, items } }), false);
  }
  valid(validate, { ...snapshot, data: { ...snapshot.data, current: false, quality: 'future-quality', gold: null, bindingRevision: null } });
  const entry = { type: snapshot.data.items[2].name.type, rawName: '原始钢琴', displayName: '原始钢琴', table: 'EquipmentTool',
    row: '1', metaOffset: '9007199254740993', nameOffset: '9007199254740994', dataOffset: '9007199254740995',
    isInstrument: true, instrumentTypeId: '18446744073709551615', equipmentToolTypeId: '1001' };
  valid(publicSchema('InventoryCatalogEntry'), entry);
  assert.equal(publicSchema('InventoryCatalogEntry')({ ...entry, instrumentTypeId: 1 }), false);
  assert.equal(publicSchema('InventoryObservationRequest')({ gameSessionId: 'game-fixture', rawInput: true }), false);
  assert.equal(publicSchema('InventoryItemQuery')({ container: 1 }), false);
  valid(publicSchema('InventoryItemQuery'), { container: null, pageSize: 10 });
});

test('inventory Worker v1 accepts old interests, hot preparation and bounded typed assets', () => {
  const validate = ajv.compile(read('contracts/worker/observation-worker.v1.schema.json'));
  const scope = { protocolVersion: 1, messageId: '1', requestId: 'a'.repeat(32), runId: 'b'.repeat(32),
    bindingRevision: '18446744073709551615', fencingToken: '0' };
  const client = { gameSessionId: 'game-fixture', processId: 100, processStartTime: '2026-11-10T00:00:00Z', region: 'TW' };
  valid(validate, { ...scope, kind: 'request', payload: { method: 'inventory.catalog', arguments: client } });
  valid(validate, { ...scope, kind: 'command', payload: { commandId: '9007199254740993', method: 'inventory.prepare', arguments: client } });
  valid(validate, { ...scope, kind: 'command', payload: { commandId: '1', method: 'observation.interests', arguments: { musicEnabled: true, combat: null } } });
  const inventory = { id: 'c'.repeat(32), profile: 'inventory-fixture' };
  valid(validate, { ...scope, kind: 'event', payload: { interestsApplied: { musicEnabled: true, combat: null, inventory } } });
  valid(validate, { ...scope, kind: 'commandResult', payload: { commandId: '1', accepted: false, code: 'inventory.buildUnsupported',
    interests: { musicEnabled: true, combat: null } } });
  const frame = { schemaVersion: 1, source: snapshot.data.source, gameSessionId: snapshot.data.gameSessionId,
    generation: snapshot.data.generation, sequence: '1', observedAt: snapshot.data.lastObservedAt, monotonicMs: 100,
    kind: 'full', identity: snapshot.data.identity, items: snapshot.data.items.map(value => value.item), changes: [],
    coverage: snapshot.data.coverage, gold: snapshot.data.gold, bindingRevision: snapshot.data.bindingRevision };
  const event = { ...scope, kind: 'event', payload: { inventory: { schemaVersion: 1, interestId: inventory.id, frame } } };
  valid(validate, event);
  for (const changes of [{ gold: 1 }, { sequence: '0' }, { bindingRevision: null }, { items: null }, { kind: 'unknown' }])
    assert.equal(validate({ ...event, payload: { inventory: { ...event.payload.inventory, frame: { ...frame, ...changes } } } }), false);
  const profile = { id: inventory.profile, source: snapshot.data.source, assemblySha256: 'd'.repeat(64), metadataSha256: 'e'.repeat(64),
    categories: ['Consumable', 'Equipment', 'EquipmentTool'], fullInventory: true, incrementalInventory: true };
  valid(validate, { ...scope, kind: 'commandResult', payload: { commandId: '1', prepared: { profile } } });
  const asset = { fileName: 'f'.repeat(32) + '.json', bytes: '67108864', sha256: 'd'.repeat(64) };
  valid(validate, { ...scope, kind: 'event', payload: { inventoryAsset: asset } });
  valid(validate, { ...scope, kind: 'result', payload: { catalogAsset: asset } });
  for (const changes of [{ fileName: '../outside.json' }, { bytes: '0' }, { bytes: '67108865' }, { bytes: 1 }, { sha256: 'bad' }])
    assert.equal(validate({ ...scope, kind: 'event', payload: { inventoryAsset: { ...asset, ...changes } } }), false);
  assert.equal(validate({ ...scope, kind: 'event', payload: { inventoryAsset: asset, inventory: event.payload.inventory } }), false);
});

test('inventory events carry bounded revision summaries and preserve v1 envelopes', () => {
  const data = {
    'inventory.snapshot.changed': { revision: snapshot.resourceRevision, accountId: 'account-1', characterId: 'character-1', current: true, snapshotRequired: true },
    'inventory.observation.degraded': { observationId: 'op-inventory', gameSessionId: 'game-fixture', revision: snapshot.resourceRevision,
      generation: '2', reasons: ['inventory.sequenceGap'], snapshotRequired: true },
    'inventory.catalog.changed': { revision: snapshot.catalog.revision, id: snapshot.catalog.id, entries: 12486, excluded: 7 }
  };
  for (const [type, value] of Object.entries(data)) {
    const schema = read('artifacts/generated/contracts/events/' + type + '.v1.schema.json');
    assert.equal(schema['x-runtime-support'], 'implemented');
    const envelope = { eventId: 'inventory-event', type, schemaVersion: 1, module: 'inventory', resourceId: 'inventory',
      resourceRevision: snapshot.resourceRevision, occurredAt: '2026-11-10T00:00:00Z', correlationId: null, data: value };
    valid(ajv.compile(schema), envelope);
    assert.equal(ajv.compile(schema)({ ...envelope, data: { ...value, revision: 1 } }), false);
    assert.ok(Buffer.byteLength(JSON.stringify(envelope)) < 65536);
  }
});

test('inventory SDK uses authorized operation routes and preserves conditional retry headers', async () => {
  const calls = [];
  const operation = { id: 'op-inventory', kind: 'inventory.catalog.import', owner: 'owner', resourceRefs: [], state: 'queued', phase: 'queued',
    revision: '1', progress: null, timestamps: { createdAt: '2026-11-10T00:00:00Z', startedAt: null, completedAt: null },
    result: null, problem: null, cancellation: { state: 'none', reason: null }, effectStatus: 'none' };
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'inventory-fixture-token', async (url, options) => {
    calls.push({ path: new URL(url).pathname, options });
    return new Response(JSON.stringify(operation), { status: 202, headers: { 'Content-Type': 'application/json' } });
  });
  const tag = '"inventory-catalog-' + snapshot.backendEpoch + '-1"';
  await sdk.startInventoryObservation({ gameSessionId: 'game-fixture', replayScenarioId: null, replayIntervalMs: 250 }, 'inventory-observe-key');
  await sdk.importInventoryCatalog({ fileSelectionId: 'selection-1' }, 'inventory-catalog-key', tag);
  await sdk.commitInventoryImport('preview-1', { resolutions: [] }, 'inventory-import-key', '"inventory-' + snapshot.backendEpoch + '-2"');
  await sdk.exportInventory({ accountId: 'account-1', includeHistory: true }, 'inventory-export-key');
  assert.deepEqual(calls.map(value => value.path), ['/api/v1/inventory/observations', '/api/v1/inventory/catalog/imports',
    '/api/v1/inventory/imports/preview-1/commit', '/api/v1/inventory/exports']);
  assert.equal(new Headers(calls[1].options.headers).get('If-Match'), tag);
  for (const call of calls) assert.ok(new Headers(call.options.headers).get('Idempotency-Key'));
  assert.deepEqual(JSON.parse(calls[1].options.body), { fileSelectionId: 'selection-1' });
});
