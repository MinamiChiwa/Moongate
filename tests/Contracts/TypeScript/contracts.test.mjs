import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { gunzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { createServer, createConnection } from 'node:net';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const require = createRequire(import.meta.url);
const root = fileURLToPath(new URL('../../../', import.meta.url));
const buildRoot = resolve(process.env.MABITOOL_TEST_ARTIFACTS_PATH ?? resolve(root, 'artifacts/build'));
const { MabiToolClient, MabiToolApiError, uint64, damageAmount } = require(resolve(root, 'artifacts/generated/sdk/typescript/compiled/client.js'));
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const fixture = read('contracts/fixtures/combat-summary.v1.json');
const openapi = read('artifacts/generated/contracts/openapi/v1.json');
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
ajv.addSchema(openapi, 'urn:mabitool:openapi');
const eventSchema = read('artifacts/generated/contracts/events/combat.summary.changed.v1.schema.json');
const validateEvent = ajv.compile(eventSchema);
const validateProblem = ajv.compile(read('artifacts/generated/contracts/problem.v1.schema.json'));
const valid = (validator, value) => assert.equal(validator(value), true, JSON.stringify(validator.errors));

test('storage capacity and transaction limit errors are registered as retryable 503 codes', () => {
  const catalog = read('artifacts/generated/contracts/error-codes.v1.json');
  for (const source of [catalog, openapi['x-error-codes']])
    for (const code of ['storage.writeQueueFull', 'storage.readCapacityExceeded', 'storage.transactionBudgetExceeded', 'storage.unavailable'])
      assert.deepEqual(source.find(item => item.code === code), { code, status: 503, retryable: true });
  valid(validateProblem, { type: 'urn:mabitool:problem:storage-writeQueueFull', title: '数据库写队列已满', status: 503, code: 'storage.writeQueueFull',
    messageKey: 'storage.writeQueueFull', traceId: 'a'.repeat(32), details: {}, retryable: true });
});

test('room score draft preserves explicit sources, nullable evidence and member revisions', () => {
  const selection = { scopeKey: 'A'.repeat(64), scoreId: 'score-local', sourceRevision: '9007199254740993', partId: null };
  const score = { entry: { id: selection.scoreId, rawTitle: '樂譜：同名曲', displayTitle: '同名曲', artistId: null,
    tags: [], sourceRefs: [{ provider: 'mml', region: 'TW', characterScope: '1:18446744073709551615', sourceId: 'file-a',
      occurrence: null, locatorCapability: 'none', ambiguous: false }], duplicateGroup: null, availability: 'imported', revision: selection.sourceRevision },
    contentSha256: null, timeline: { id: selection.scoreId, fullCycleMs: 5000, selectedPartCycleMs: null, loop: false,
      tailMs: 0, calculatorVersion: 'history-v1', sourceHash: 'a'.repeat(64), quality: 'estimated', evidenceKind: 'imported-history-estimate',
      partId: null, observedAt: '2026-11-09T00:00:00Z' }, fingerprint: 'f'.repeat(64), unavailableReason: null };
  const snapshot = { backendEpoch: 'a'.repeat(32), serverEpoch: '00000000-0000-0000-0000-000000000001', roomCode: 'ABC123',
    memberId: 'member-a', supported: true, revision: '1', draft: { schemaVersion: 1, id: 'draft-a', revision: '18446744073709551615',
      source: selection, score, assignments: [{ memberId: 'member-a', partId: null, instrumentId: 'piano' }] },
    members: [{ memberId: 'member-a', state: 'pending', reason: 'room.scoreContentUnknown', draftRevision: '18446744073709551615',
      contentSha256: null, partId: null, instrumentId: 'piano' }], local: null };
  const validate = ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/RoomScoreDraftSnapshot' });
  valid(validate, snapshot);
  assert.equal(snapshot.draft.score.contentSha256, null);
  assert.equal(snapshot.draft.score.entry.rawTitle, '樂譜：同名曲');
  assert.equal(validate({ ...snapshot, draft: { ...snapshot.draft, revision: 9007199254740993 } }), false);
  const publish = ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/RoomScoreDraftRequest' });
  valid(publish, { score: selection, assignments: snapshot.draft.assignments });
  assert.equal(publish({ score: { ...selection, sourceRevision: 1 }, assignments: snapshot.draft.assignments }), false);
  const mapping = ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/RoomScoreMappingRequest' });
  valid(mapping, { score: null, instrumentId: null });
  assert.equal(mapping({ score: selection, instrumentId: 'piano', memberId: 'another-member' }), false);
  const schema = read('artifacts/generated/contracts/events/room.score-draft.changed.v1.schema.json');
  assert.equal(schema['x-runtime-support'], 'implemented');
  valid(ajv.compile(schema), { eventId: 'score-event', type: 'room.score-draft.changed', schemaVersion: 1, module: 'rooms',
    resourceId: 'room-score:member-a', resourceRevision: '1', occurredAt: '2026-11-09T00:00:00Z', correlationId: null,
    data: { owner: 'member-a', snapshot } });
  const protocol = ajv.compile(read('artifacts/generated/contracts/room/score-draft.v1.schema.json'));
  valid(protocol, { type: 'publish-score-draft', requestId: 'publish-a', expectedRevision: '0', source: selection, score, assignments: snapshot.draft.assignments });
  const memberMapping = { ...snapshot.members[0], draftRevision: '1' };
  valid(protocol, { type: 'map-score-draft', draftId: 'draft-a', draftRevision: '1', mapping: memberMapping });
  assert.equal(protocol({ type: 'map-score-draft', draftId: 'draft-a', draftRevision: '1', mapping: { ...memberMapping, state: 'unassigned' } }), false);
  assert.equal(protocol({ type: 'clear-score-draft', draftId: 'draft-a', draftRevision: '1', keyCode: 27 }), false);
  const action = { id: 'action-a', sessionId: 'round-a', kind: 'start', targetServerMs: 5000,
    serverEpoch: snapshot.serverEpoch, scoreDraftId: 'draft-a', scoreDraftRevision: '1' };
  valid(protocol, { type: 'prepare-action', mode: 'ensemble', action });
  assert.equal(protocol({ type: 'prepare-action', mode: 'ensemble', action: { ...action, scoreDraftRevision: 1 } }), false);
});

test('room score draft SDK sends conditional persistent writes and maps only the caller', async () => {
  const calls = [];
  const operation = { id: 'op-score', kind: 'room.scoreDraft.publish', owner: 'owner', resourceRefs: [{ type: 'room-score-draft', id: 'current' }],
    state: 'queued', phase: 'queued', revision: '1', progress: null, timestamps: { createdAt: '2026-11-09T00:00:00Z', startedAt: null, completedAt: null },
    result: null, problem: null, cancellation: { state: 'none', reason: null }, effectStatus: 'none' };
  const transport = async (url, options) => {
    calls.push({ path: new URL(url).pathname, options });
    return new Response(JSON.stringify(operation), { status: 202, headers: { 'Content-Type': 'application/json', ETag: '"op-score-1"' } });
  };
  const sdk = new MabiToolClient('http://127.0.0.1:3134', () => 'fixture-token', transport);
  const score = { scopeKey: 'A'.repeat(64), scoreId: 'local-a', sourceRevision: '1', partId: null };
  const tag = '"room-score-' + 'a'.repeat(32) + '-' + 'b'.repeat(32) + '-1"';
  assert.equal((await sdk.publishRoomScoreDraft({ score, assignments: [{ memberId: 'member-a', partId: null, instrumentId: 'piano' }] }, 'publish-score', tag)).id, 'op-score');
  await sdk.mapRoomScoreDraft({ score, instrumentId: 'piano' }, 'map-score', tag);
  await sdk.clearRoomScoreDraft('clear-score', tag);
  assert.deepEqual(calls.map(call => call.path), ['/api/v1/rooms/score-draft/publish', '/api/v1/rooms/score-draft/map', '/api/v1/rooms/score-draft/clear']);
  for (const call of calls) {
    assert.equal(new Headers(call.options.headers).get('If-Match'), tag);
    assert.ok(new Headers(call.options.headers).get('Idempotency-Key'));
  }
  assert.deepEqual(JSON.parse(calls[1].options.body), { score, instrumentId: 'piano' });
});

function bootstrap(discovery) {
  return new Promise((resolve, reject) => {
    const socket = createConnection('\\\\.\\pipe\\' + discovery.pipeName);
    let buffer = Buffer.alloc(0); let completed = false;
    socket.setTimeout(5000, () => socket.destroy(new Error('IPC timeout')));
    socket.on('error', reject);
    socket.on('connect', () => {
      const payload = Buffer.from(JSON.stringify({ protocolVersion: 1, clientName: 'node-contract-test' }));
      const header = Buffer.alloc(4); header.writeInt32LE(payload.length);
      socket.write(Buffer.concat([header, payload]));
    });
    socket.on('data', data => {
      buffer = Buffer.concat([buffer, data]);
      if (buffer.length > 16388 || (buffer.length >= 4 && (buffer.readInt32LE() <= 0 || buffer.readInt32LE() > 16384))) {
        socket.destroy(new Error('Invalid IPC size')); return;
      }
      if (buffer.length >= 4 && buffer.length >= buffer.readInt32LE() + 4) {
        try { completed = true; resolve(JSON.parse(buffer.subarray(4).toString('utf8'))); socket.end(); }
        catch (error) { socket.destroy(error); }
      }
    });
    socket.on('end', () => { if (!completed) reject(new Error('IPC response is incomplete')); });
  });
}

test('color Worker envelope validates scope, UInt64 boundaries and granted assets', () => {
  const schema = read('contracts/worker/color-worker.v1.schema.json');
  const validate = ajv.compile(schema);
  const control = { protocolVersion: 1, messageId: '1', requestId: null, kind: 'ping', runId: null, bindingRevision: null, fencingToken: null, payload: {} };
  valid(validate, control);
  const request = { ...control, kind: 'request', requestId: 'a'.repeat(32), runId: 'b'.repeat(32), bindingRevision: '18446744073709551615', fencingToken: '0', payload: { method: 'color.evaluate', arguments: {} } };
  valid(validate, request);
  assert.equal(validate({ ...request, payload: {} }), false);
  for (const value of ['', '0', '01', '1\n', '18446744073709551616']) assert.equal(validate({ ...request, messageId: value }), false);
  for (const changes of [{ unknown: true }, { runId: null }, { requestId: 'a'.repeat(32) + '\n' }, { fencingToken: 1 }, { protocolVersion: 2 }]) assert.equal(validate({ ...request, ...changes }), false);
  assert.equal(validate({ ...control, runId: request.runId }), false);
  const validatePayload = ajv.compile({ $ref: schema.$id + '#/$defs/request' });
  valid(validatePayload, request.payload);
  const reference = { assetId: 'c'.repeat(32), runId: request.runId, sizeBytes: 128, sha256: 'd'.repeat(64), expiresAtUnixMs: 1794141543000 };
  valid(validatePayload, { method: 'color.evaluate', argumentsAsset: reference });
  assert.equal(validatePayload({ ...request.payload, argumentsAsset: reference }), false);
  assert.equal(validatePayload({ method: 'color.evaluate', argumentsAsset: { ...reference, sizeBytes: -1 } }), false);
  const port = { ...request, kind: 'portRequest', payload: { callId: '1', method: 'game.capture', arguments: {} } };
  valid(validate, port);
  valid(validate, { ...port, payload: { ...port.payload, method: 'game.nativeAccess' } });
  valid(validate, { ...port, kind: 'portResult', payload: { callId: '1', result: { width: 200, height: 120, asset: reference } } });
  valid(validate, { ...port, kind: 'portError', payload: { callId: '1', code: 'input.runStopped' } });
  for (const payload of [{ ...port.payload, callId: '0' }, { ...port.payload, method: 'raw.input' },
    { ...port.payload, arguments: [] }, { ...port.payload, extra: true }]) assert.equal(validate({ ...port, payload }), false);
  assert.equal(validate({ ...port, runId: null }), false);
  assert.equal(validate({ ...port, kind: 'portResult', payload: { callId: '1', result: null } }), false);
  assert.equal(validate({ ...port, kind: 'portError', payload: { callId: '1', code: 1 } }), false);
  const command = { ...request, kind: 'command', payload: { commandId: '1', action: 'retry', revision: '18446744073709551615' } };
  valid(validate, command);
  valid(validate, { ...command, payload: { ...command.payload, action: 'select', batchId: 'board-0', candidateId: 'choice' } });
  for (const changes of [{ commandId: '0' }, { revision: 1 }, { batchId: 'extra' }, { action: 'select' }, { action: 'confirm' }])
    assert.equal(validate({ ...command, payload: { ...command.payload, ...changes } }), false);
  const ack = { ...command, kind: 'commandResult', payload: { commandId: '1', accepted: true, code: null,
    interaction: { revision: '2', batchId: null, selectionEnabled: false, retryEnabled: false } } };
  valid(validate, ack);
  valid(validate, { ...ack, payload: { ...ack.payload, accepted: false, code: 'color.runInactive', interaction: null } });
  assert.equal(validate({ ...ack, payload: { ...ack.payload, code: 'color.runInactive' } }), false);
  assert.equal(validate({ ...ack, payload: { ...ack.payload, accepted: false } }), false);
});

test('observation Worker validates passive scope, identity, timeline and source', () => {
  const validate = ajv.compile(read('contracts/worker/observation-worker.v1.schema.json'));
  const scope = { protocolVersion: 1, messageId: '1', requestId: 'a'.repeat(32), runId: 'b'.repeat(32),
    bindingRevision: '9007199254740993', fencingToken: '0' };
  const capture = { ...scope, kind: 'request', payload: { method: 'observation.capture', arguments: {
    gameSessionId: 'game-fixture', region: 'TW', processId: 100, processStartTime: '2026-11-09T00:00:00Z',
    channelId: 'c'.repeat(32), profile: 'tw-music-v1' } } };
  valid(validate, capture);
  valid(validate, { ...capture, payload: { method: 'observation.channels', arguments: {} } });
  for (const changes of [{ profile: 'kr-combat-v1' }, { region: 'KR' }, { processId: 0 }, { channelId: 'c'.repeat(32) + '\n' },
    { processId: '100' }, { rawInput: true }]) {
    assert.equal(validate({ ...capture, payload: { ...capture.payload, arguments: { ...capture.payload.arguments, ...changes } } }), false);
  }
  const identity = { characterId: '9007199254740993', realmId: '3', nickname: '原始角色名', actorId: '18446744073709551615' };
  const music = { concertId: '7', performerCharacterId: identity.characterId, hostCharacterId: identity.characterId,
    title: '同名乐谱', isLoop: true, selectedChannels: [0], scoreSha256: 'd'.repeat(64), wholeScoreDurationSeconds: 1,
    selectedDurationSeconds: .5, estimatedStartMonotonicMs: 2500, timingSource: 'tw-receive-offset-v1', playbackTimingVerified: false, issue: null };
  const report = { value: { captureGeneration: 'd'.repeat(32), identity, concert: { id: '7', hostCharacterId: identity.characterId,
    orderedCharacterIds: [identity.characterId], members: [identity] }, music, packets: '18446744073709551615', detail: '被动摘要' },
    sequence: '1', running: true, monotonicMs: 1000, observedAt: '2026-11-09T00:00:00Z', reason: null,
    source: { provider: 'observation-fixture', providerVersion: '1.0.0', region: 'TW', gameBuild: 'synthetic-v1' } };
  const event = { ...scope, kind: 'event', payload: { report } }; valid(validate, event);
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/GameCaptureReport' }), report);
  for (const changes of [{ value: null }, { sequence: '0' }, { monotonicMs: -1 }, { source: { ...report.source, region: 'KR' } }])
    assert.equal(validate({ ...event, payload: { report: { ...report, ...changes } } }), false);
  for (const changes of [{ characterId: 9007199254740993 }, { actorId: '18446744073709551616' }, { realmId: 'foreign' }, { nickname: null }])
    assert.equal(validate({ ...event, payload: { report: { ...report, value: { ...report.value, identity: { ...identity, ...changes } } } } }), false);
  for (const changes of [{ selectedChannels: [0, 0] }, { selectedChannels: [128] }, { playbackTimingVerified: true }, { wholeScoreDurationSeconds: 604801 }])
    assert.equal(validate({ ...event, payload: { report: { ...report, value: { ...report.value, music: { ...music, ...changes } } } } }), false);
  assert.equal(validate({ ...capture, kind: 'portRequest', payload: { method: 'game.input', arguments: {} } }), false);
});

test('observation Worker capture diagnostics stay integer-only with fixed codes and bounded maps', () => {
  const validate = ajv.getSchema('urn:mabitool:worker:observation:v1');
  const scope = { protocolVersion: 1, messageId: '2', requestId: 'a'.repeat(32), runId: 'b'.repeat(32),
    bindingRevision: '9007199254740993', fencingToken: '0' };
  const counters = read('contracts/fixtures/observation-capture-diagnostics.v1.json');
  const event = { ...scope, kind: 'event', payload: { captureDiagnostics: counters } };
  valid(validate, event);
  const types = Object.fromEntries(Array.from({ length: 24 }, (_, i) => [String(20000 + i), '1']));
  valid(validate, { ...event, payload: { captureDiagnostics: { ...counters, frames: { ...counters.frames, types } } } });
  for (const mutate of [
    x => { x.connections.current = 1; },
    x => { x.packets.remoteAddress = '127.0.0.1'; },
    x => { x.connections.remotePort = '1111'; },
    x => { x.reassembly.resets.freeText = '1'; },
    x => { x.frames.rejected.parser = '1'; },
    x => { x.frames.types['020590'] = '1'; },
    x => { x.frames.types.op20590 = '1'; },
    x => { x.frames.types['20000'] = '-1'; },
    x => { Object.assign(x.frames.types, types, { '30000': '1' }); },
    x => { delete x.queues.combatPeak; },
    x => { x.devices[0].name = 'Realtek PCIe GbE'; },
    x => { x.devices[0].kind = 'wifi'; },
    x => { x.devices[2].issue = 'Realtek PCIe GbE'; },
    x => { x.read.mode = 'busy'; },
    x => { x.schemaVersion = 2; }
  ]) {
    const changed = structuredClone(counters); mutate(changed);
    assert.equal(validate({ ...event, payload: { captureDiagnostics: changed } }), false, JSON.stringify(changed));
  }
  assert.equal(validate({ ...event, payload: { captureDiagnostics: counters, report: {} } }), false);
});

test('observation Worker combat frames carry optional bounded healing entries from the sanitized samples', () => {
  const validate = ajv.getSchema('urn:mabitool:worker:observation:v1');
  const healing = read('contracts/fixtures/combat-tw-healing.v1.json');
  assert.equal(healing.protocolProfile, 'tw-combat-20261110-v2');
  for (const sample of healing.samples) {
    const kinds = kind => sample.frames.filter(frame => frame.kind === kind);
    assert.equal(kinds('received').length, sample.expected.received);
    assert.equal(kinds('received').reduce((sum, frame) => sum + frame.amount, 0), sample.expected.receivedAmount);
    assert.equal(kinds('cast').length, sample.expected.cast);
    assert.equal(kinds('health').length, sample.expected.localHealth);
  }
  const scope = { protocolVersion: 1, messageId: '3', requestId: 'a'.repeat(32), runId: 'b'.repeat(32),
    bindingRevision: '9007199254740993', fencingToken: '0' };
  const capture = { ...scope, kind: 'request', payload: { method: 'observation.capture', arguments: {
    gameSessionId: 'game-fixture', region: 'TW', processId: 100, processStartTime: '2026-11-09T00:00:00Z',
    channelId: 'c'.repeat(32), profile: 'tw-combat-20261110-v2' } } };
  valid(validate, capture);
  valid(validate, { ...capture, payload: { ...capture.payload, arguments: { ...capture.payload.arguments, profile: 'tw-combat-20261110-v1' } } });
  const key = { gameSessionId: 'game-fixture', observationGeneration: '1', rawInstanceId: '9007199254740993', spawnGeneration: null };
  const at = '2026-11-09T00:00:01Z';
  const received = { eventId: 'heal-1-0', direction: 'received', targetEntity: null, flags: '8192', amount: '1232',
    effectiveAmount: '68', overheal: '1164', receiptMerged: false, observedAt: at, monotonicMs: 1000, sequence: '0', quality: 'confirmed' };
  const cast = { ...received, eventId: 'heal-1-25', direction: 'cast', targetEntity: key, flags: '10240', amount: '320',
    effectiveAmount: null, overheal: null, receiptMerged: true, effectiveReason: 'hp-unpaired' };
  const frame = { schemaVersion: 1, source: { provider: 'tw-combat', providerVersion: '1.0.0', region: 'TW', gameBuild: 'synthetic-v1' },
    gameSessionId: 'game-fixture', generation: '1', sequence: '1', observedAt: at, monotonicMs: 1000,
    entities: [], damage: [], effects: [], reconciliations: [], gaps: [], healing: [received, cast] };
  const event = { ...scope, kind: 'event', payload: { combat: { schemaVersion: 1, interestId: 'd'.repeat(32), localEntity: null, frame } } };
  valid(validate, event);
  const { healing: _, ...withoutHealing } = frame;
  valid(validate, { ...event, payload: { combat: { ...event.payload.combat, frame: withoutHealing } } });
  for (const change of [{ amount: '0' }, { amount: 1232 }, { flags: '-1' }, { effectiveAmount: '1e2' }, { rawSkill: '1' }])
    assert.equal(validate({ ...event, payload: { combat: { ...event.payload.combat, frame: { ...frame, healing: [{ ...received, ...change }] } } } }), false, JSON.stringify(change));
  assert.equal(validate({ ...event, payload: { combat: { ...event.payload.combat, frame: { ...frame, healing: Array(257).fill(received) } } } }), false);
});

test('shared C#/TypeScript fixture preserves full UInt64, nullable fields and enums', () => {
  valid(validateEvent, fixture);
  valid(validateEvent, read('artifacts/generated/contracts/fixtures/csharp-combat-summary.v1.json'));
  assert.deepEqual(JSON.parse(JSON.stringify(fixture)), fixture);
  assert.deepEqual(read('artifacts/generated/contracts/fixtures/csharp-combat-summary.v1.json'), fixture);
  assert.equal(fixture.data.localEntity.rawInstanceId, '9007199254740993');
  assert.equal(uint64(fixture.data.attributedDamage), '18446744073709551615');
  for (const mutate of [
    x => { x.data.localEntity.rawInstanceId = 9007199254740992; },
    x => { x.data.attributedDamage = '1' + '0'.repeat(128); },
    x => { x.data.quality = 1; },
    x => { x.data = null; },
    x => { delete x.data.encounterDps; },
    x => { x.type = 'other.changed'; },
    x => { x.module = 'other'; },
    x => { x.schemaVersion = 2; },
    x => { x.data.healing.castAmount = 4037; },
    x => { x.data.healing.receivedEffective = '-1'; },
    x => { delete x.data.healing.receivedCount; }
  ]) {
    const changed = structuredClone(fixture); mutate(changed);
    assert.equal(validateEvent(changed), false, JSON.stringify(changed));
  }
  const future = structuredClone(fixture); future.data.quality = 'future-quality';
  valid(validateEvent, future);
  assert.deepEqual(JSON.parse(JSON.stringify(future)), future);
  const accumulated = structuredClone(fixture); accumulated.data.attributedDamage = '36893488147419103230';
  valid(validateEvent, accumulated); assert.equal(damageAmount(accumulated.data.attributedDamage), accumulated.data.attributedDamage);
  for (const value of ['-1', '01', '1e2', '1\n', '1' + '0'.repeat(128)]) assert.throws(() => damageAmount(value), TypeError);
});

test('UInt64 schema and runtime reject all noncanonical and overflow boundaries', () => {
  const schemaCheck = ajv.compile(openapi.components.schemas.UInt64String);
  const samples = ['', '00', '01', '-1', '+1', ' 1', '1 ', '1\n', '1\r\n', '１', '1e2', '18446744073709551616', '99999999999999999999'];
  for (const value of samples) {
    assert.throws(() => uint64(value), TypeError, value);
    assert.equal(schemaCheck(value), false, value);
  }
  const maximum = 18446744073709551615n;
  const values = [0n, 1n, 9n, 10n, 9007199254740993n, maximum - 1n, maximum];
  for (let i = 0; i < 20; i++) {
    const place = 10n ** BigInt(i);
    const prefix = maximum / place * place;
    values.push(prefix - 1n, prefix, prefix + place - 1n, prefix + place);
  }
  let state = 173n;
  for (let i = 0; i < 1000; i++) { state = (state * 6364136223846793005n + 1n) % (10n ** 21n); values.push(state); }
  for (const value of values.filter(v => v >= 0)) {
    const text = value.toString();
    assert.equal(schemaCheck(text), value <= maximum, text);
    if (value <= maximum) assert.equal(uint64(text), text);
    else assert.throws(() => uint64(text), TypeError);
  }
});

test('all OpenAPI references resolve and all published event schemas compile', () => {
  const walk = node => {
    if (!node || typeof node !== 'object') return;
    if (typeof node.$ref === 'string' && node.$ref.startsWith('#/')) {
      let target = openapi;
      for (const part of node.$ref.slice(2).split('/')) target = target?.[part.replaceAll('~1', '/').replaceAll('~0', '~')];
      assert.notEqual(target, undefined, node.$ref);
    }
    for (const item of Object.values(node)) walk(item);
  };
  walk(openapi);
  for (const name of Object.keys(openapi.components.schemas)) ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/' + name });
  for (const type of openapi['x-event-types']) {
    const schema = read('artifacts/generated/contracts/events/' + type + '.v1.schema.json');
    assert.equal(schema['x-runtime-support'], ['shortcut.triggered', 'diagnostics.record', 'operation.changed', 'module.configuration-changed', 'replay.observation-changed', 'music.catalog.changed', 'music.library.changed', 'music.playback-history.changed', 'music.import.committed', 'room.import.committed', 'color.import.committed', 'room.preferences.changed', 'room.score-draft.changed', 'color.preferences.changed', 'color.run.changed', 'color.library.changed', 'game.observation.changed', 'room.session.changed', 'room.server.changed', 'desktop.surface.changed', 'combat.summary.changed', 'combat.effects.changed', 'combat.encounter.closed', 'combat.observation.degraded', 'combat.measurement.changed', 'inventory.snapshot.changed', 'inventory.observation.degraded', 'inventory.catalog.changed', 'storage.changed', 'storage.backup.changed', 'storage.maintenance.changed', 'storage.module-data.changed', 'update.policy-changed', 'update.progress'].includes(type) ? 'implemented' : 'schema-only');
    assert.equal(typeof ajv.compile(schema), 'function');
  }
  assert.equal(openapi.openapi, '3.1.0');
  assert.equal(openapi.components.schemas.ApiProblem['x-runtime-support'], undefined);
});

test('SDK validates endpoints and path IDs, preserves cancellation and transport policy', async () => {
  for (const url of ['https://localhost:19998', 'http://example.com:19998', 'http://localhost', 'http://secret@localhost:19998', 'http://localhost:19998/?token=secret', 'http://localhost:19998/path'])
    assert.throws(() => new MabiToolClient(url, () => 'fixture'), TypeError);
  let calls = 0;
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async (url, options) => {
    calls++;
    assert.equal(options.redirect, 'error');
    assert.equal(options.credentials, 'omit');
    assert.equal(url.search, '');
    assert.equal(options.headers.Authorization, 'Bearer fixture');
    options.signal?.throwIfAborted();
    return Response.json({ status: 'healthy', instanceId: 'fixture' });
  });
  for (const id of ['', '../core', 'core\n', 'CORE', 'core?token=secret']) assert.throws(() => sdk.getModuleSettingsSchema(id), TypeError);
  assert.equal(calls, 0);
  await sdk.getHealth();
  const controller = new AbortController(); controller.abort();
  await assert.rejects(sdk.getHealth(controller.signal), { name: 'AbortError' });
});

test('color capture SDK sends JSON for empty POST and preserves pixel and release contracts', async () => {
  const capture = { id: '123capture', bounds: { x: -2, y: -1, width: 2, height: 2 }, encoding: 'rgb24',
    sha256: 'a'.repeat(64), sizeBytes: 12, capturedAt: '2026-11-09T00:00:00Z', expiresAt: '2026-11-09T00:01:00Z' };
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async (url, options) => {
    if (options.method === 'DELETE') { assert.equal(options.body, undefined); return Response.json({ captureId: capture.id, released: true }); }
    assert.equal(options.headers['Content-Type'], 'application/json');
    if (url.pathname.endsWith('/pixels')) {
      assert.deepEqual(JSON.parse(options.body), { x: -2, y: -1 });
      return Response.json({ captureId: capture.id, x: -2, y: -1, hex: '#202122' });
    }
    assert.deepEqual(JSON.parse(options.body), {});
    return Response.json(capture);
  });
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorScreenCapture' }), await sdk.captureColorScreen());
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorPickedPixel' }), await sdk.pickColorPixel(capture.id, { x: -2, y: -1 }));
  assert.equal((await sdk.releaseColorCapture(capture.id)).released, true);
});

test('update install example submits one explicit request and discovery recovery reads its original operation', async () => {
  const { submitUpdateInstall, reconnectUpdateOperation } = await import('../../../frontends/examples/update-client.mjs');
  const planId = 'b'.repeat(32), operationId = 'op-' + 'a'.repeat(32);
  const operation = { id: operationId, kind: 'update.apply', owner: 'paired-client', resourceRefs: [{ type: 'update-plan', id: planId }],
    state: 'succeeded', phase: 'committed', revision: '9', progress: 1,
    timestamps: { createdAt: '2026-11-09T00:00:00Z', startedAt: '2026-11-09T00:00:01Z', completedAt: '2026-11-09T00:00:02Z' },
    result: { planId, transactionId: 'c'.repeat(32), releaseId: 'release-2', outcome: 'committed', recoverySnapshotId: null, failureCode: null },
    problem: null, cancellation: { state: 'none', reason: null }, effectStatus: 'none' };
  const writes = [], reads = [];
  const sdk = new MabiToolClient('http://localhost:19998', () => 'paired-token', async (url, options) => {
    if (options.method === 'POST' && url.pathname.endsWith('/apply')) {
      writes.push(url.pathname); assert.equal(options.headers['Idempotency-Key'], 'one-update-install');
      assert.equal(options.headers['If-Match'], '"update-plan-' + planId + '-1"'); return Response.json(operation, { status: 202 });
    }
    reads.push(url.pathname); assert.equal(options.method, 'GET');
    if (url.pathname.endsWith('/system/info')) return Response.json({ instanceId: 'new-epoch' });
    if (url.pathname.endsWith('/event-snapshot')) return Response.json({ eventCursor: 'new-cursor', resources: [] });
    if (url.pathname.endsWith('/updates/state')) return Response.json({ installedReleaseId: 'release-2' });
    assert.ok(url.pathname.endsWith('/operations/' + operationId)); return Response.json(operation);
  });
  const accepted = await submitUpdateInstall(sdk, { plan: { id: planId }, etag: '"update-plan-' + planId + '-1"' },
    { idempotencyKey: 'one-update-install' });
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/OperationSnapshot' }), accepted);
  let connections = 0;
  const recovered = await reconnectUpdateOperation(async () => { if (++connections === 1) throw new TypeError('宿主正在退出'); return sdk; }, accepted.id);
  assert.equal(connections, 2); assert.equal(recovered.operation.id, accepted.id); assert.equal(recovered.instanceId, 'new-epoch');
  assert.equal(recovered.state.installedReleaseId, 'release-2'); assert.equal(writes.length, 1);
  assert.ok(reads.some(path => path.endsWith('/operations/' + operationId)));
});

test('color presence and runtime SDK preserve explicit renewal and 64-bit resource values', async () => {
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async (url, options) => {
    if (url.pathname.endsWith('/presence')) {
      assert.equal(options.method, 'POST'); assert.deepEqual(JSON.parse(options.body), {});
      return Response.json({ runId: 'run-1', hostClockEpoch: 'epoch', validForMs: 5000 });
    }
    assert.equal(options.method, 'GET'); assert.equal(options.body, undefined);
    if (url.pathname.endsWith('/runtime')) return Response.json({ state: 'warm', maximumParallelism: 2, idleRetentionSeconds: 60,
      maximumConcurrentRequests: 1, startedWorkers: '18446744073709551615', workerWorkingSetBytes: '9007199254740993' });
    return Response.json({ items: [{ actionId: 'emergency-stop', configured: { key: 'F9', control: false, alt: false, shift: false, windows: false },
      registered: { key: 'F8', control: false, alt: false, shift: false, windows: false }, status: 'registered', failureCode: null, usedFallback: true }],
      nextCursor: null, snapshotRevision: 'shortcuts:fixture' });
  });
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorInteractionPresence' }), await sdk.renewColorPresence('run-1'));
  const runtime = await sdk.getColorRuntime();
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorEngineRuntime' }), runtime);
  assert.equal(runtime.workerWorkingSetBytes, '9007199254740993');
  assert.equal((await sdk.getShortcuts()).items[0].registered.key, 'F8');
  assert.equal(openapi.paths['/api/v1/color/runs/{id}/presence'].post['x-required-permission'], 'color.control');
});

test('observation SDK preserves ETag, idempotency and independent passive permission', async () => {
  const snapshot = { backendEpoch: 'a'.repeat(32), revision: '9007199254740993', captureId: null, state: 'stopped',
    gameSessionId: null, bindingRevision: null, channelId: null, profile: null, subscriberCount: 0, latest: null, reason: null };
  const etag = '"game-observation-' + snapshot.backendEpoch + '-' + snapshot.revision + '"';
  const request = { channelId: 'b'.repeat(32), bindingRevision: '18446744073709551615', profile: 'tw-music-v1' };
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async (url, options) => {
    if (url.pathname.endsWith('/channels/query')) {
      assert.equal(options.method, 'POST'); assert.deepEqual(JSON.parse(options.body), {});
      return Response.json({ items: [{ id: request.channelId, description: '合成通道', addresses: ['127.0.0.1'] }], nextCursor: null, snapshotRevision: 'epoch' });
    }
    if (options.method === 'POST') {
      assert.equal(options.headers['Idempotency-Key'], 'observation-sdk'); assert.equal(options.headers['If-Match'], etag);
      assert.deepEqual(JSON.parse(options.body), request);
      return Response.json({ id: 'c'.repeat(32), captureId: 'd'.repeat(32), snapshot: { ...snapshot, state: 'starting', subscriberCount: 1 } }, { status: 201 });
    }
    if (options.method === 'DELETE') {
      assert.equal(options.body, undefined); return Response.json({ id: 'c'.repeat(32), released: true, snapshot });
    }
    return Response.json(snapshot, { headers: { etag } });
  });
  let tag; valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/GameObservationSnapshot' }),
    await sdk.getGameObservation(undefined, metadata => { tag = metadata.etag; }));
  assert.equal(tag, etag);
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ApiListOfCaptureChannelOption' }), await sdk.queryObservationChannels());
  valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/GameObservationSubscription' }),
    await sdk.startGameObservation(request, 'observation-sdk', tag));
  assert.equal((await sdk.releaseGameObservation('c'.repeat(32))).released, true);
  assert.equal(openapi.paths['/api/v1/game/observations'].post['x-required-permission'], 'game.observe');
  assert.equal(openapi.paths['/api/v1/game/observations/runtime'].get['x-required-permission'], 'system.read');
  assert.equal(openapi.paths['/api/v1/diagnostics/runtime'].get['x-required-permission'], 'system.read');
  assert.equal(openapi.paths['/api/v1/diagnostics/records/query'].post['x-required-permission'], 'system.read');
  assert.equal(openapi.paths['/api/v1/game/window-activation'].post['x-required-permission'], 'game.control');
  assert.equal(openapi.paths['/api/v1/game/window-activation'].post.operationId, 'activateGameWindow');
  assert.deepEqual(openapi.components.schemas.DiagnosticRecordQuery.properties.order.type, 'string');
  assert.equal(openapi.components.schemas.UpdatePolicy.properties.maximumBytesPerSecond.type.includes('null'), true);
  for (const [path, method] of [['/api/v1/diagnostics/captures', 'post'], ['/api/v1/diagnostics/captures/current', 'get'], ['/api/v1/diagnostics/captures/{id}/stop', 'post']])
    assert.equal(openapi.paths[path][method]['x-required-permission'], 'diagnostics.debug');
});

test('SDK returns typed HTTP errors, tolerates malformed problems and bounds responses', async () => {
  const problem = { type: 'urn:mabitool:problem:auth-required', title: '需要凭据', status: 401, code: 'auth.required', messageKey: 'auth.required', traceId: 'fixture', details: {}, retryable: false };
  valid(validateProblem, problem);
  for (const body of [JSON.stringify(problem), '{bad']) {
    const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async () => new Response(body, { status: 401, headers: { 'content-type': 'application/problem+json' } }));
    await assert.rejects(sdk.getHealth(), error => error instanceof MabiToolApiError && error.status === 401 && error.problem?.code === (body === '{bad' ? undefined : 'auth.required'));
  }
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', async () => new Response(new Uint8Array(4 * 1024 * 1024 + 1)));
  await assert.rejects(sdk.getHealth(), /exceeds size limit/);
});

test('SSE SDK parses fragmented UTF-8, multiline JSON and controls, bounds frames and releases streams', async () => {
  const wire = '\uFEFF: connected\r\nid: cursor\r\nevent: snapshot\r\ndata: {\r\ndata: "value":"台服🎵"}\r\n\r\n: heartbeat\r\nevent: control\rdata: {"code":"auth.required"}\r\rdata: {"unfinished":true}';
  const bytes = new TextEncoder().encode(wire); let index = 0; let cancelled = false;
  const transport = async (url, options) => {
    assert.equal(options.headers['X-Event-Topics'], 'operation.changed');
    assert.equal(options.headers['Last-Event-ID'], 'previous');
    return new Response(new ReadableStream({
      pull(controller) { if (index === bytes.length) controller.close(); else { controller.enqueue(bytes.slice(index, index + 7)); index = Math.min(index + 7, bytes.length); } },
      cancel() { cancelled = true; }
    }), { headers: { 'content-type': 'text/event-stream' } });
  };
  const sdk = new MabiToolClient('http://localhost:19998', () => 'fixture', transport);
  const values = [];
  for await (const item of sdk.streamEvents({ topics: ['operation.changed'], resourceIds: [], lastEventId: 'previous' })) values.push(item);
  assert.equal(values.length, 2); assert.equal(values[0].data.value, '台服🎵');
  assert.equal(values[1].id, 'cursor'); assert.equal(values[1].event, 'control');
  index = 0;
  for await (const item of sdk.streamEvents({ topics: ['operation.changed'], resourceIds: [], lastEventId: 'previous' })) { assert.equal(item.event, 'snapshot'); break; }
  assert.equal(cancelled, true);
  const large = new MabiToolClient('http://localhost:19998', () => 'fixture', async () => new Response('data: ' + 'x'.repeat(4 * 1024 * 1024), { headers: { 'content-type': 'text/event-stream' } }));
  await assert.rejects(large.streamEvents().next(), /frame exceeds size limit/);
  const badUtf8 = new MabiToolClient('http://localhost:19998', () => 'fixture', async () => new Response(new Uint8Array([255]), { headers: { 'content-type': 'text/event-stream' } }));
  await assert.rejects(badUtf8.streamEvents().next(), TypeError);
});

test('binary SDK bounds declared and actual content and releases cancelled downloads', async () => {
  for (const mode of ['declared-limit', 'actual-length', 'truncated', 'cancel']) {
    let pulled = 0, cancelled = false;
    const source = new ReadableStream({
      pull(controller) { if (pulled++ === 0) controller.enqueue(new Uint8Array([1, 2])); else controller.close(); },
      cancel() { cancelled = true; }
    }, { highWaterMark: 0 });
    const length = mode === 'declared-limit' ? String(256 * 1024 * 1024 + 1) : mode === 'actual-length' ? '1' : '3';
    const sdk = new MabiToolClient('http://127.0.0.1:19998', () => 'binary-sdk-test', async () =>
      new Response(source, { headers: { 'content-length': length } }));
    if (mode === 'declared-limit') {
      await assert.rejects(sdk.downloadAsset('a'.repeat(32)), /size limit/); assert.equal(pulled, 0); assert.equal(cancelled, true);
    } else {
      const read = await sdk.downloadAsset('a'.repeat(32));
      if (mode === 'cancel') { await read.content.cancel(); assert.equal(pulled, 0); assert.equal(cancelled, true); }
      else await assert.rejects(new Response(read.content).arrayBuffer(), /length|size limit/);
    }
  }
});

test('TypeScript SDK and JSON Schemas agree with actual authenticated loopback host', async () => {
  const socket = createServer(); socket.listen(0, '127.0.0.1'); await once(socket, 'listening');
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  await mkdir(resolve(root, 'artifacts/tmp'), { recursive: true });
  const temporary = await mkdtemp(resolve(root, 'artifacts/tmp/auth-ts-'));
  const configuration = process.env.MABITOOL_TEST_CONFIGURATION ?? 'Release';
  const dll = resolve(buildRoot, 'bin/MabiTool.Host', configuration.toLowerCase(), 'MabiTool.Host.dll');
  const environment = { ...process.env, MABITOOL_API_PORT: String(port), MABITOOL_ALLOWED_ORIGINS: '', MABITOOL_RUNTIME_DIRECTORY: resolve(temporary, 'runtime'), MABITOOL_BOOTSTRAP_DIRECTORY: resolve(temporary, 'bootstrap') };
  delete environment.MABITOOL_API_TOKEN;
  const child = spawn('dotnet', [dll, '--initialize-data', '--data-root', resolve(temporary, 'data')], { cwd: root, windowsHide: true, env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  let diagnostics = '';
  child.stdout.on('data', value => { diagnostics += value; }); child.stderr.on('data', value => { diagnostics += value; });
  try {
    const deadline = Date.now() + 60000;
    let discovery;
    for (;;) {
      try { discovery = JSON.parse(readFileSync(resolve(temporary, 'runtime/instance.json'), 'utf8')); break; }
      catch (error) {
        if (child.exitCode !== null || Date.now() >= deadline) throw new Error('Host failed to start: ' + diagnostics, { cause: error });
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
    const handshake = await bootstrap(discovery);
    const token = handshake.grant.token;
    assert.equal(typeof token, 'string'); assert.equal(token.length, 43);
    assert.equal(handshake.instanceId, discovery.instanceId);
    const sdk = new MabiToolClient(discovery.apiBaseUri, () => token);
    const streamOptions = { topics: ['module.configuration-changed'], resourceIds: ['color'], lastEventId: null };
    const events = sdk.streamEvents(streamOptions, AbortSignal.timeout(20000));
    const first = await events.next();
    assert.equal(first.value.event, 'snapshot');
    const initialCursor = first.value.id;
    assert.deepEqual(await sdk.getOpenApi(), openapi);
    let configurationMetadata;
    const configuration = await sdk.getModuleConfiguration('color', undefined, value => { configurationMetadata = value; });
    assert.equal(configuration.enabled, true);
    assert.equal(configurationMetadata.etag, '"module-color-0"');
    let operationMetadata;
    const operation = await sdk.configureModule('color', { enabled: false }, 'typescript-operation', configurationMetadata.etag, undefined, value => { operationMetadata = value; });
    assert.equal(operationMetadata.status, 202);
    assert.equal(operationMetadata.location, '/api/v1/operations/' + operation.id);
    assert.equal((await sdk.configureModule('color', { enabled: false }, 'typescript-operation', configurationMetadata.etag)).id, operation.id);
    await assert.rejects(sdk.configureModule('color', { enabled: true }, 'typescript-operation', configurationMetadata.etag), error => error.status === 409 && error.problem.code === 'idempotency.conflict');
    const operationDeadline = Date.now() + 10000;
    let final;
    do {
      final = await sdk.getOperation(operation.id);
      if (['succeeded', 'failed', 'interrupted', 'cancelled'].includes(final.state)) break;
      await new Promise(resolve => setTimeout(resolve, 20));
    } while (Date.now() < operationDeadline);
    assert.equal(final.state, 'succeeded');
    assert.deepEqual(await sdk.cancelOperation(operation.id, { reason: null }), final);
    assert.equal((await sdk.getModuleConfiguration('color')).enabled, false);
    const changed = await events.next();
    assert.equal(changed.value.event, 'module.configuration-changed');
    assert.equal(changed.value.data.data.enabled, false);
    valid(ajv.compile(read('artifacts/generated/contracts/events/module.configuration-changed.v1.schema.json')), changed.value.data);
    const replay = sdk.streamEvents({ ...streamOptions, lastEventId: initialCursor }, AbortSignal.timeout(10000));
    assert.equal((await replay.next()).value.id, changed.value.id);
    await replay.return(); await events.return();
    assert.equal((await sdk.getEventSnapshot(streamOptions)).resources[0].resourceRevision, '1');
    let musicMetadata;
    const music = await sdk.getMusicLibrary(undefined, value => { musicMetadata = value; });
    assert.equal(music.revision, '0');
    const musicEvents = sdk.streamEvents({ topics: ['music.library.changed'], resourceIds: [], lastEventId: null }, AbortSignal.timeout(10000));
    assert.equal((await musicEvents.next()).value.event, 'snapshot');
    const musicRequest = {
      putFolders: [{ id: 'folder-ts', parentId: null, name: '繁體資料夾', sortKey: '1' }], deleteFolders: [],
      putPlaylists: [{ id: 'playlist-ts-a', folderId: 'folder-ts', name: '甲', notes: '備註', defaultInstrumentId: 'piano', sortKey: '1', items: [] },
        { id: 'playlist-ts-b', folderId: 'folder-ts', name: '乙', notes: '', defaultInstrumentId: null, sortKey: '2', items: [] }],
      deletePlaylists: [], nameSettings: null
    };
    const edit = await sdk.editMusicLibrary(musicRequest, 'typescript-music-edit', musicMetadata.etag);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/MusicEditResult' }), edit);
    assert.equal(edit.revision, '1');
    assert.deepEqual(await sdk.editMusicLibrary(musicRequest, 'typescript-music-edit', musicMetadata.etag), edit);
    const musicQuery = { collection: 'playlists', parentId: 'folder-ts', playlistId: null, scopeKey: null, search: '', pageSize: 1, cursor: null };
    const musicFirst = await sdk.queryMusicLibrary(musicQuery);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/MusicLibraryPage' }), musicFirst);
    assert.equal(musicFirst.playlists[0].id, 'playlist-ts-a');
    assert.equal(musicFirst.playlists[0].defaultInstrumentId, 'piano');
    assert.equal((await sdk.queryMusicLibrary({ ...musicQuery, cursor: musicFirst.nextCursor })).playlists[0].id, 'playlist-ts-b');
    await assert.rejects(sdk.editMusicLibrary(musicRequest, 'typescript-music-stale', musicMetadata.etag), error => error.status === 412);
    const musicChanged = (await musicEvents.next()).value;
    assert.equal(musicChanged.event, 'music.library.changed');
    assert.equal(musicChanged.data.data.revision, '1');
    await musicEvents.return();
    const importRequest = {
      sourceId: 'typescript-backup', sourceLabel: '原曲庫', region: 'TW', realmId: 'realm', characterId: '18446744073709551615',
      files: [{ name: 'settings.json', contentBase64: Buffer.from(JSON.stringify({ gap_sec: 4, advance_margin: 3 })).toString('base64') }], artistMappings: null
    };
    const importPreview = await sdk.previewMusicImport(importRequest, 'typescript-import-preview');
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/MusicImportPreview' }), importPreview);
    assert.equal(importPreview.expectedLibraryRevision, '1');
    const importOperation = await sdk.commitMusicImport(importPreview.id, 'typescript-import-commit', '"music-library-1"');
    let importCompleted = importOperation;
    for (let attempt = 0; attempt < 100 && !['succeeded', 'failed', 'cancelled', 'interrupted'].includes(importCompleted.state); attempt++) {
      await new Promise(resolve => setTimeout(resolve, 20)); importCompleted = await sdk.getOperation(importOperation.id);
    }
    assert.equal(importCompleted.state, 'succeeded');
    assert.equal(importCompleted.result.importId, importPreview.id);
    assert.equal((await sdk.getMusicImport(importPreview.id)).state, 'committed');
    assert.equal((await sdk.getMusicPreferences()).gapSeconds, 4);
    assert.equal((await sdk.commitMusicImport(importPreview.id, 'typescript-import-commit', '"music-library-1"')).id, importOperation.id);
    let colorMetadata;
    const colorLibrary = await sdk.getColorLibrary(undefined, value => { colorMetadata = value; });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorLibrary' }), colorLibrary);
    const profile = { ...colorLibrary.draft, id: '123-ts-color', name: '繁體配色方案' };
    const savedColor = await sdk.editColorLibrary({ putProfiles: [profile], deleteProfileIds: [], draft: null }, 'typescript-color-save', colorMetadata.etag);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ColorLibraryChanged' }), savedColor);
    assert.equal((await sdk.getColorProfile(profile.id)).name, profile.name);
    assert.deepEqual(await sdk.editColorLibrary({ putProfiles: [profile], deleteProfileIds: [], draft: null }, 'typescript-color-save', colorMetadata.etag), savedColor);
    const colorHistory = await sdk.queryColorHistory({ pageSize: 20, beforeOperationId: null });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ApiListOfColorHistoryItem' }), colorHistory);
    assert.deepEqual(colorHistory.items, []);
    const settingsFixture = read('tests/Contracts/MabiTool.Host.Tests/Fixtures/settings-import.v1.json');
    const settingsPreviews = new Map();
    for (const [project, previewMethod, commitMethod, queryMethod] of [
      ['mabisync', 'previewRoomImport', 'commitRoomImport', 'getRoomImport'],
      ['color-studio', 'previewColorImport', 'commitColorImport', 'getColorImport']]) {
      const body = { sourceId: 'typescript-settings-backup', sourceLabel: '原設定',
        files: Object.entries(settingsFixture[project]).map(([name, value]) => ({ name, contentBase64: Buffer.from(JSON.stringify(value)).toString('base64') })),
        bindingOverrides: null, profileNames: null };
      const preview = await sdk[previewMethod](body, 'typescript-settings-' + project);
      valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/SettingsImportPreview' }), preview);
      assert.equal(preview.state, 'ready');
      let operation = await sdk[commitMethod](preview.id, 'typescript-commit-' + project, preview.targetETag);
      for (let attempt = 0; attempt < 100 && !['succeeded', 'failed', 'cancelled', 'interrupted'].includes(operation.state); attempt++) {
        await new Promise(resolve => setTimeout(resolve, 20)); operation = await sdk.getOperation(operation.id);
      }
      assert.equal(operation.state, 'succeeded');
      valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/SettingsImportReceipt' }), operation.result);
      assert.equal((await sdk[queryMethod](preview.id)).state, 'committed');
      assert.equal((await sdk[previewMethod](body, 'typescript-repeat-' + project)).id, preview.id);
      settingsPreviews.set(project, preview);
    }
    assert.equal((await sdk.getRoomPreferences()).timingOffsetMs, -25);
    assert.equal((await sdk.getRoomPreferences()).requiresGameBinding, true);
    const importedColorHistory = await sdk.queryImportedColorHistory({ pageSize: 20, beforeId: null });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ApiListOfImportedColorHistoryItem' }), importedColorHistory);
    assert.equal(importedColorHistory.items[0].quality, 'estimated');
    assert.equal(importedColorHistory.items[0].localTime, '2026-11-08 21:00:00');
    const presentation = await sdk.queryPresentation({ topics: ['room', 'color'], resourceIds: null });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/PresentationSnapshot' }), presentation);
    assert.equal(presentation.backendEpoch, presentation.clock.hostClockEpoch);
    assert.equal(presentation.subscription.lastEventId, presentation.eventCursor);
    assert.deepEqual(presentation.subscription.topics, ['color.library.changed', 'color.run.changed', 'room.session.changed']);
    assert.equal(presentation.resources.find(resource => resource.type === 'room.session.changed').data.session.state, 'disconnected');
    const desktop = await sdk.getDesktopBridge();
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/DesktopBridgeStatus' }), desktop);
    assert.equal(desktop.state, 'unavailable');
    assert.equal(desktop.reason, 'desktop.bridgeMissing');
    const invalidCursor = sdk.streamEvents({ ...streamOptions, lastEventId: 'bad' });
    await assert.rejects(invalidCursor.next(), error => error instanceof MabiToolApiError && error.status === 409 && error.problem.code === 'events.resyncRequired');
    await assert.rejects(sdk.configureModule('color', { enabled: true }, 'typescript-stale', configurationMetadata.etag), error => error.status === 412 && error.problem.code === 'revision.stale');
    const runtimeOverview = await sdk.getDiagnosticRuntime();
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/DiagnosticRuntimeSnapshot' }), runtimeOverview);
    assert.equal(runtimeOverview.schemaVersion, 1);
    assert.equal(runtimeOverview.probes.find(probe => probe.id === 'observation.capture').state, 'stopped');
    assert.ok(runtimeOverview.probes.every(probe => probe.state !== 'unavailable'));
    let diagnosticOperation;
    for (let attempt = 0; attempt < 10 && !diagnosticOperation; attempt++) {
      const source = (await sdk.getDiagnostics()).sessions[0];
      assert.ok(source);
      try {
        diagnosticOperation = await sdk.exportDiagnostics({ sessions: [{ id: source.id, revision: source.revision }] },
          'typescript-diagnostic-export-' + attempt);
      } catch (error) {
        if (!(error instanceof MabiToolApiError) || error.problem?.code !== 'revision.stale') throw error;
      }
    }
    assert.ok(diagnosticOperation);
    const diagnosticDeadline = Date.now() + 15000;
    while (!['succeeded', 'failed', 'cancelled', 'interrupted'].includes(diagnosticOperation.state) && Date.now() < diagnosticDeadline) {
      await new Promise(resolve => setTimeout(resolve, 20)); diagnosticOperation = await sdk.getOperation(diagnosticOperation.id);
    }
    assert.equal(diagnosticOperation.state, 'succeeded');
    const diagnosticArtifact = diagnosticOperation.result;
    const diagnosticChunk = await sdk.readDiagnosticExport(diagnosticArtifact.id, { offset: 0, count: 262144 });
    const diagnosticBytes = Buffer.from(diagnosticChunk.contentBase64, 'base64');
    assert.equal(BigInt(diagnosticBytes.length), BigInt(diagnosticArtifact.asset.sizeBytes));
    assert.equal(createHash('sha256').update(diagnosticBytes).digest('hex'), diagnosticArtifact.asset.sha256);
    const diagnosticDocument = JSON.parse(gunzipSync(diagnosticBytes, { maxOutputLength: 16 * 1024 * 1024 }).toString('utf8'));
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/DiagnosticExportDocument' }), diagnosticDocument);
    assert.equal(diagnosticDocument.sessions.length, 1);
    const selectedBytes = new TextEncoder().encode('{"theme":"dark"}');
    let uploadMetadata;
    const upload = await sdk.createFileUpload({ name: 'settings.json', sizeBytes: String(selectedBytes.length),
      sha256: createHash('sha256').update(selectedBytes).digest('hex'), mediaType: 'application/json' }, 'typescript-file-upload',
      undefined, metadata => { uploadMetadata = metadata; });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/FileUploadSnapshot' }), upload);
    const uploadInput = new ReadableStream({ start(controller) {
      controller.enqueue(selectedBytes.subarray(0, 5)); controller.enqueue(selectedBytes.subarray(5)); controller.close();
    } });
    assert.ok(uploadMetadata.etag);
    const readyUpload = await sdk.uploadFileContent(upload.id, uploadInput, 'typescript-file-content', uploadMetadata.etag);
    assert.equal(readyUpload.state, 'ready');
    const filePreview = await sdk.previewFileImport({ project: 'color-studio', sourceId: 'typescript-streamed', sourceLabel: '原設定',
      uploadIds: [upload.id], archiveUploadId: null, scope: null, artistMappings: null, bindingOverrides: null, profileNames: null }, 'typescript-file-preview');
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/FileImportPreview' }), filePreview);
    const download = await sdk.downloadAsset(readyUpload.asset.id);
    assert.equal(download.sizeBytes, String(selectedBytes.length)); assert.equal(download.sha256, readyUpload.sha256);
    assert.deepEqual(new Uint8Array(await new Response(download.content).arrayBuffer()), selectedBytes);
    const streamedDiagnostic = await sdk.downloadAsset(diagnosticArtifact.asset.id);
    assert.equal(createHash('sha256').update(Buffer.from(await new Response(streamedDiagnostic.content).arrayBuffer())).digest('hex'), diagnosticArtifact.asset.sha256);
    for (const [path, definition] of Object.entries(openapi.paths)) {
      if (!definition.get || definition.get.operationId === 'streamEvents') continue;
      if (path === '/api/v1/assets/{id}/content') continue;
      if (path === '/api/v1/assets/{id}' || path === '/api/v1/uploads/{id}' || path === '/api/v1/imports/{id}/preview') {
        const id = path.includes('/assets/') ? readyUpload.asset.id : path.includes('/uploads/') ? upload.id : filePreview.id;
        const actual = await sdk[definition.get.operationId](id);
        const schema = Object.values(definition.get.responses['200'].content)[0].schema;
        valid(ajv.compile({ $ref: 'urn:mabitool:openapi' + schema.$ref }), actual); continue;
      }
      if (path === '/api/v1/files/selections/{id}' || path === '/api/v1/exports/{id}') {
        await assert.rejects(sdk[definition.get.operationId]('a'.repeat(32)), error => error.status === 404);
        continue;
      }
      if (path === '/api/v1/music/performance/sessions/{id}') continue;
      if (path === '/api/v1/updates/plans/{id}') {
        await assert.rejects(sdk.getUpdatePlan('a'.repeat(32)), error => {
          assert.equal(error.problem.code, 'update.sourceNotConfigured'); valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (path.startsWith('/api/v1/storage/cleanup-plans/{id}')) {
        await assert.rejects(sdk[definition.get.operationId]('missing'), error => {
          assert.equal(error.status, 404); valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (/^\/api\/v1\/combat\/(observations\/\{id\}|encounters\/\{id\}(\/timeline)?|entities\/\{id\}\/effects)$/.test(path)) {
        await assert.rejects(sdk[definition.get.operationId]('missing'), error => {
          assert.equal(error.status, 404);
          if (path.includes('/entities/')) assert.equal(error.problem.code, 'combat.entityOutOfScope');
          valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (['/api/v1/inventory/observations/{id}', '/api/v1/inventory/accounts/{id}/associations', '/api/v1/inventory/imports/{id}'].includes(path)) {
        await assert.rejects(sdk[definition.get.operationId]('missing'), error => {
          assert.equal(error.status, 404); assert.equal(error.problem.code, 'resource.notFound');
          valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (path === '/api/v1/music/imports/{id}') {
        const actual = await sdk.getMusicImport(importPreview.id);
        valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/MusicImportPreview' }), actual); continue;
      }
      if (path === '/api/v1/rooms/imports/{id}' || path === '/api/v1/color/imports/{id}') {
        const project = path.includes('/rooms/') ? 'mabisync' : 'color-studio';
        const actual = await sdk[definition.get.operationId](settingsPreviews.get(project).id);
        valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/SettingsImportPreview' }), actual); continue;
      }
      if (path === '/api/v1/rooms/invitation') continue; // Requires an active owner room.
      if (path === '/api/v1/diagnostics/exports/{id}') {
        const actual = await sdk.getDiagnosticExport(diagnosticArtifact.id);
        valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/DiagnosticExportArtifact' }), actual); continue;
      }
      if (path === '/api/v1/desktop/surfaces/{id}') {
        await assert.rejects(sdk.getDesktopSurface('missing'), error => {
          assert.equal(error.status, 404); valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (/^\/api\/v1\/color\/(runs|profiles|history|assets)\/\{id\}$/.test(path)) {
        await assert.rejects(sdk[definition.get.operationId]('missing'), error => {
          assert.equal(error.status, 404); valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (/^\/api\/v1\/storage\/(backups|restore-plans|migration-plans|directory-selections|module-deletion-plans)\/\{id\}$/.test(path)) {
        await assert.rejects(sdk[definition.get.operationId]('missing'), error => {
          assert.equal(error.status, 404); valid(validateProblem, error.problem); return true;
        });
        continue;
      }
      if (path === '/api/v1/storage/module-data/{id}') {
        valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/StorageModuleDataSnapshot' }), await sdk.getStorageModuleData('color'));
        continue;
      }
      const actual = path.includes('{id}') ? await sdk[definition.get.operationId](definition.get.operationId === 'getOperation' ? operation.id : 'core') : await sdk[definition.get.operationId]();
      const response = definition.get.responses['200'].content;
      const schema = Object.values(response)[0].schema;
      if (schema.$ref) valid(ajv.compile({ $ref: 'urn:mabitool:openapi' + schema.$ref }), actual);
    }
    await assert.rejects(sdk.getModuleSettingsSchema('missing'), error => {
      assert.equal(error.status, 404); valid(validateProblem, error.problem); return true;
    });
    const denied = new MabiToolClient('http://127.0.0.1:' + port, () => 'invalid');
    await assert.rejects(denied.getOpenApi(), error => {
      assert.equal(error.status, 401); valid(validateProblem, error.problem); return true;
    });
    const challenge = await sdk.createPairingChallenge({ clientName: 'typescript-native', origin: null, permissions: ['system.read'] });
    const anonymous = new MabiToolClient('http://127.0.0.1:' + port, () => { throw new Error('Anonymous pairing must not request a bearer'); });
    const grant = await anonymous.redeemPairing({ challengeId: challenge.challengeId, code: challenge.code });
    assert.notEqual(grant.token, token);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/AccessGrant' }), grant);
    const paired = new MabiToolClient('http://127.0.0.1:' + port, () => grant.token);
    assert.equal((await paired.getAuthSession()).clientId, grant.session.clientId);
    await assert.rejects(paired.getAuthClients(), error => error.status === 403 && error.problem.code === 'permission.denied');
    const pairedEvents = paired.streamEvents(undefined, AbortSignal.timeout(10000));
    assert.equal((await pairedEvents.next()).value.event, 'snapshot');
    assert.equal((await sdk.revokeClient(grant.session.clientId)).revoked, true);
    assert.equal((await pairedEvents.next()).value.data.code, 'auth.required');
    assert.equal((await pairedEvents.next()).done, true);
    await assert.rejects(paired.getHealth(), error => error.status === 401);
    await assert.rejects(anonymous.redeemPairing({ challengeId: challenge.challengeId, code: challenge.code }), error => error.problem.code === 'auth.pairingInvalid');
    assert.equal(diagnostics.includes(token), false);
    assert.equal(diagnostics.includes(challenge.code), false);
    assert.equal(diagnostics.includes(grant.token), false);
    const discoveryText = readFileSync(resolve(temporary, 'runtime/instance.json'), 'utf8');
    assert.equal(discoveryText.includes(token), false);
    assert.equal(discoveryText.includes(grant.token), false);
  } finally {
    if (child.exitCode === null) { child.kill(); await once(child, 'exit'); }
    await rm(temporary, { recursive: true, force: true });
  }
});


test('mock backend runs frontend replay example, schemas and CLI over authenticated IPC and SSE', async () => {
  const temporary = await mkdtemp(resolve(root, 'artifacts/tmp/mock-ts-'));
  const configuration = process.env.MABITOOL_TEST_CONFIGURATION ?? 'Release';
  const dll = resolve(buildRoot, 'bin/MabiTool.Host', configuration.toLowerCase(), 'MabiTool.Host.dll');
  const environment = { ...process.env, MABITOOL_API_PORT: '0', MABITOOL_ALLOWED_ORIGINS: '' };
  delete environment.MABITOOL_API_TOKEN;
  const start = () => spawn('dotnet', [dll, '--mock', '--portable-root', temporary, '--initialize-data'],
    { cwd: root, windowsHide: true, env: environment, stdio: ['ignore', 'pipe', 'pipe'] });
  let child = start(), diagnostics = '';
  const capture = process => { process.stdout.on('data', value => { diagnostics += value; }); process.stderr.on('data', value => { diagnostics += value; }); };
  capture(child);
  const instance = resolve(temporary, 'runtime/instance.json');
  async function discovery(previous = null) {
    const deadline = Date.now() + 15000;
    for (;;) {
      try { const value = JSON.parse(readFileSync(instance, 'utf8')); if (value.instanceId !== previous) return value; }
      catch { }
      if (child.exitCode !== null || Date.now() >= deadline) throw new Error('Mock startup failed: ' + diagnostics);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
  try {
    const info = await discovery(); const handshake = await bootstrap(info);
    const sdk = new MabiToolClient(info.apiBaseUri, () => handshake.grant.token);
    const { runReplayExample } = await import('../../../frontends/examples/replay-client.mjs');
    const observationSchema = ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/ObservationOfReplayState' });
    const envelopeSchema = ajv.compile(read('artifacts/generated/contracts/events/replay.observation-changed.v1.schema.json'));
    const states = [];
    const result = await runReplayExample(sdk, { scenario: 'partial', intervalMs: 500, onState: value => states.push(value) });
    assert.equal(result.operation.state, 'succeeded'); assert.equal(result.disconnected, true);
    assert.deepEqual(result.steps, [1, 2, 3]);
    assert.equal(states[1].quality, 'partial'); assert.equal(states[1].state.monsterEffects.complete, false);
    assert.equal(states[1].state.combat.encounterDps, null);
    valid(observationSchema, result.operation.result);
    assert.equal(states[0].state.combat.localEntity.rawInstanceId, '9007199254740993');
    const filter = { topics: ['replay.observation-changed'], resourceIds: [result.operation.id], lastEventId: null };
    const snapshot = await sdk.getEventSnapshot(filter);
    valid(observationSchema, snapshot.resources[0].data);
    // 实际 SSE 初始快照和恢复的事件均校验，不使用与实现重复的手写结果。
    const failure = await sdk.startReplay({ scenarioId: 'failure', intervalMs: 500 }, 'ts-failure');
    const events = sdk.streamEvents({ ...filter, resourceIds: [failure.id] }, AbortSignal.timeout(10000));
    assert.equal((await events.next()).value.event, 'snapshot');
    valid(envelopeSchema, (await events.next()).value.data); await events.return();
    let failed; const failureDeadline = Date.now() + 10000;
    do { failed = await sdk.getOperation(failure.id); if (failed.state === 'failed') break; await new Promise(resolve => setTimeout(resolve, 20)); } while (Date.now() < failureDeadline);
    assert.equal(failed.state, 'failed');
    assert.equal(failed.problem.code, 'provider.unavailable');
    const { runCombatExample } = await import('../../../frontends/examples/combat-client.mjs');
    const combatStates = [], targetEffects = [];
    const combat = await runCombatExample(sdk, { onState: value => combatStates.push(value), onEffects: value => targetEffects.push(value) });
    assert.equal(combat.operation.state, 'succeeded'); assert.equal(combat.disconnected, true); assert.equal(combat.reconnected, true);
    assert.equal(combat.snapshot.data.encounter.summary.attributedDamage, '3600');
    const combatSummary = combat.snapshot.data.encounter.summary;
    assert.ok(combatSummary.durationMs > 0);
    assert.equal(combatSummary.encounterDps, 3600 / (combatSummary.durationMs / 1000));
    assert.equal(new Set(combat.revisions).size, combat.revisions.length);
    assert.ok(combatStates.length > 0);
    assert.equal(combatStates.at(-1).data.encounter.summary.attributedDamage, '3600');
    for (let index = 1; index < combatStates.length; index++)
      assert.ok(BigInt(combatStates[index].resourceRevision) > BigInt(combatStates[index - 1].resourceRevision));
    for (const value of combatStates) valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/CombatSnapshot' }), value);
    const targets = combat.snapshot.data.entities.filter(x => x.role === 'monster');
    assert.equal(targets.length, 2); assert.equal(targets[0].rawName, targets[1].rawName);
    assert.notEqual(targets[0].key.rawInstanceId, targets[1].key.rawInstanceId);
    const secondCombatClient = new MabiToolClient(info.apiBaseUri, () => handshake.grant.token);
    assert.deepEqual((await secondCombatClient.getCombatSnapshot()).data, combat.snapshot.data);
    assert.notEqual((await sdk.getCombatEntityEffects(targets[0].id)).views[0].instance.instanceId,
      (await secondCombatClient.getCombatEntityEffects(targets[1].id)).views[0].instance.instanceId);
    assert.ok(targetEffects.length >= 2);
    const roundTerminal = async id => {
      const deadline = Date.now() + 10000;
      for (;;) {
        const operation = await sdk.getOperation(id);
        if (['succeeded', 'failed', 'cancelled', 'interrupted'].includes(operation.state)) return operation;
        assert.ok(Date.now() < deadline, 'Room operation timed out');
        await new Promise(resolve => setTimeout(resolve, 20));
      }
    };
    let serverTag;
    await sdk.getRoomServer(undefined, metadata => { serverTag = metadata.etag; });
    const startedServer = await sdk.startRoomServer({ mode: 'loopback' }, 'ts-room-server', serverTag);
    assert.equal((await roundTerminal(startedServer.id)).state, 'succeeded');
    const localServer = await sdk.getRoomServer();
    const localRoom = await sdk.createRoom({ endpoint: localServer.localEndpoint, nickname: '模拟房主' }, 'ts-create-room');
    await sdk.setRoomReadiness({ ready: true, simulation: true, canStart: false, canStop: false });
    let roundTag;
    await sdk.getCurrentRoomRound(undefined, metadata => { roundTag = metadata.etag; });
    const roundRequest = { mode: 'emote', participantIds: [localRoom.memberId], countdownMs: 60000, stopRule: 'manual' };
    const initialRoundTag = roundTag;
    const preparedRound = await sdk.prepareRoomRound(roundRequest, 'ts-prepare-round', initialRoundTag);
    const preparedResult = await roundTerminal(preparedRound.id);
    assert.equal(preparedResult.state, 'succeeded'); assert.equal(preparedResult.result.serverConfirmed, true);
    assert.equal(preparedResult.effectStatus, 'unknown');
    let roomRound;
    const readyDeadline = Date.now() + 10000;
    do {
      roomRound = await sdk.getCurrentRoomRound(undefined, metadata => { roundTag = metadata.etag; });
      if (roomRound.phase === 'committed') break;
      assert.ok(Date.now() < readyDeadline, 'Room readiness did not commit');
      await new Promise(resolve => setTimeout(resolve, 20));
    } while (true);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/RoomRoundSnapshot' }), roomRound);
    const cancelledRound = await sdk.controlRoomRound(roomRound.id, { action: 'cancel' }, 'ts-cancel-round', roundTag);
    const cancelledResult = await roundTerminal(cancelledRound.id);
    assert.equal(cancelledResult.state, 'succeeded'); assert.equal(cancelledResult.result.round.phase, 'cancelled');
    assert.equal((await secondCombatClient.getOperation(cancelledRound.id)).id, cancelledRound.id);
    assert.equal((await sdk.prepareRoomRound(roundRequest, 'ts-prepare-round', initialRoundTag)).id, preparedRound.id);
    await sdk.getRoomServer(undefined, metadata => { serverTag = metadata.etag; });
    const stoppedServer = await sdk.stopRoomServer({}, 'ts-stop-room-server', serverTag);
    assert.equal((await roundTerminal(stoppedServer.id)).state, 'succeeded');
    const { runStorageExample, runStorageBackupExample, previewStorageMaintenance, previewStorageModuleDeletion,
      submitStorageModuleDeletion, resumeStorageOperation } = await import('../../../frontends/examples/storage-client.mjs');
    const storage = await runStorageExample(sdk, { apply: true });
    assert.equal(storage.result.state, 'completed'); assert.equal(storage.result.planId, storage.plan.id);
    for (const [type, value] of [['StorageUsageSnapshot', storage.usage], ['StorageCleanupPlan', storage.plan], ['StorageCleanupResult', storage.result]])
      valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/' + type }), value);
    const savedBackup = await runStorageBackupExample(sdk, { disconnectOnce: true, idempotencyKey: 'ts-storage-backup' });
    assert.equal(savedBackup.operation.state, 'succeeded'); assert.equal(savedBackup.backup.state, 'complete');
    assert.equal(savedBackup.backup.purpose, 'manual');
    assert.equal((await sdk.createStorageBackup({}, 'ts-storage-backup')).id, savedBackup.operation.id);
    const maintenance = await previewStorageMaintenance(sdk, 'restore', { backupId: savedBackup.backup.id }, { idempotencyKey: 'ts-storage-restore' });
    assert.equal(maintenance.plan.backup.id, savedBackup.backup.id); assert.ok(maintenance.etag);
    for (const [type, value] of [['OperationSnapshot', savedBackup.operation], ['StorageBackupSummary', savedBackup.backup], ['StorageMaintenancePlan', maintenance.plan]])
      valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/' + type }), value);
    const moduleConfiguration = await sdk.getModuleConfiguration('color');
    const disabled = await sdk.configureModule('color', { enabled: false }, 'ts-disable-color', '"module-color-' + moduleConfiguration.revision + '"');
    assert.equal((await resumeStorageOperation(sdk, disabled.id)).operation.state, 'succeeded');
    const deletion = await previewStorageModuleDeletion(sdk, 'color', { idempotencyKey: 'ts-module-preview' });
    assert.deepEqual(deletion.plan.data.blockingReasons, []);
    const deleteOperation = await submitStorageModuleDeletion(sdk, deletion, { idempotencyKey: 'ts-module-delete' });
    const deleted = (await resumeStorageOperation(sdk, deleteOperation.id, { disconnectOnce: true })).operation;
    assert.equal(deleted.state, 'succeeded'); assert.equal(deleted.result.moduleId, 'color');
    assert.equal((await submitStorageModuleDeletion(sdk, deletion, { idempotencyKey: 'ts-module-delete' })).id, deleteOperation.id);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/StorageModuleDeletionPlan' }), deletion.plan);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/StorageModuleDeletionResult' }), deleted.result);
    const { saveUpdatePolicyExample, runUpdateCheckExample } = await import('../../../frontends/examples/update-client.mjs');
    const savedPolicy = await saveUpdatePolicyExample(sdk, { automaticChecks: false, checkIntervalHours: 2,
      downloadConcurrency: 1, maximumBytesPerSecond: 1048576 }, { idempotencyKey: 'ts-update-policy' });
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/UpdatePolicySnapshot' }), savedPolicy);
    assert.deepEqual(await secondCombatClient.getUpdatePolicy(), savedPolicy);
    const unlimitedPolicy = await saveUpdatePolicyExample(sdk, { automaticChecks: false, checkIntervalHours: 2,
      downloadConcurrency: 1, maximumBytesPerSecond: null }, { idempotencyKey: 'ts-update-policy-unlimited' });
    assert.equal(unlimitedPolicy.policy.maximumBytesPerSecond, null);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/UpdatePolicySnapshot' }), unlimitedPolicy);
    const update = await runUpdateCheckExample(sdk, { idempotencyKey: 'ts-update-check', disconnectOnce: true });
    assert.equal(update.operation.state, 'failed'); assert.equal(update.operation.problem.code, 'update.sourceNotConfigured');
    assert.equal(update.plan, null); assert.equal(update.state.configured, false);
    assert.equal((await secondCombatClient.getUpdateState()).operation.id, update.operation.id);
    valid(ajv.compile({ $ref: 'urn:mabitool:openapi#/components/schemas/UpdateStateSnapshot' }), update.state);
    // 执行完整命令行示例，凭据只经 IPC 获取。
    const demo = spawn(process.execPath, ['scripts/build/replay-demo.mjs', instance, 'normal'], { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '', errors = ''; demo.stdout.on('data', value => { output += value; }); demo.stderr.on('data', value => { errors += value; });
    const [exit] = await once(demo, 'exit'); assert.equal(exit, 0, errors);
    const summary = JSON.parse(output.trim().split(/\r?\n/).at(-1));
    assert.equal(summary.state, 'succeeded'); assert.equal(summary.disconnected, true); assert.deepEqual(summary.steps, [1, 2, 3]);
    assert.equal(output.includes(handshake.grant.token), false);
    const pending = await sdk.startReplay({ scenarioId: 'normal', intervalMs: 2000 }, 'ts-restart');
    const oldFilter = { topics: ['operation.changed'], resourceIds: [pending.id], lastEventId: null };
    const old = await sdk.getEventSnapshot(oldFilter);
    child.kill(); await once(child, 'exit'); child = start(); capture(child);
    const restarted = await discovery(info.instanceId); const nextHandshake = await bootstrap(restarted);
    const next = new MabiToolClient(restarted.apiBaseUri, () => nextHandshake.grant.token);
    const interrupted = await next.getOperation(pending.id);
    assert.equal(interrupted.state, 'interrupted');
    const stale = next.streamEvents({ ...oldFilter, lastEventId: old.eventCursor });
    await assert.rejects(stale.next(), error => error.problem.code === 'events.resyncRequired');
    await assert.rejects(new MabiToolClient(restarted.apiBaseUri, () => handshake.grant.token).getHealth(), error => error.status === 401);
    assert.equal((await next.getReplayCatalog()).mode, 'mock');
    // 新 epoch 读取持久投影，原回放不重新排队。
    const restored = await next.getEventSnapshot({ ...filter, lastEventId: null });
    assert.equal(restored.resources[0].data.value.step, 3);
    assert.equal((await next.getEventSnapshot({ ...filter, resourceIds: [pending.id] })).resources.length, 0);
    assert.equal(diagnostics.includes(handshake.grant.token), false);
  } finally {
    if (child.exitCode === null) { child.kill(); await once(child, 'exit'); }
    await rm(temporary, { recursive: true, force: true });
  }
});
