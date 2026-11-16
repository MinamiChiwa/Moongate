import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const read = path => JSON.parse(readFileSync(new URL('../../../' + path, import.meta.url), 'utf8'));
const fixture = read('contracts/fixtures/combat-detail.v1.json');
const require = createRequire(import.meta.url);
const { MabiToolClient, uint64 } = require('../../../artifacts/generated/sdk/typescript/compiled/client.js');
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);
ajv.addSchema(read('artifacts/generated/contracts/openapi/v1.json'), 'urn:combat-detail:openapi');
const groupSchema = ajv.compile({ $ref: 'urn:combat-detail:openapi#/components/schemas/CombatDamageGroup' });
const frameSchema = ajv.compile({ $ref: 'urn:combat-detail:openapi#/components/schemas/CombatFrame' });
const workerSchema = ajv.compile(read('contracts/worker/observation-worker.v1.schema.json'));
const valid = (schema, value) => assert.equal(schema(value), true, JSON.stringify(schema.errors));

test('shared combat detail preserves decimals, nullable evidence and full width identifiers', () => {
  valid(groupSchema, fixture.group); valid(frameSchema, fixture.frame);
  assert.deepEqual(read('artifacts/generated/contracts/fixtures/csharp-combat-detail.v1.json'), fixture);
  assert.equal(uint64(fixture.group.maximumDamage), '18446744073709551615');
  assert.equal(uint64(fixture.group.criticalEligibleCount), '1');
  assert.equal(uint64(fixture.group.mainHitEligibleCount), '1');
  assert.deepEqual(fixture.group.classificationCounts, { direct: '1', indirect: '0', damageOverTime: '0', unknown: '0' });
  assert.equal(fixture.frame.damage[0].classification, 'direct');
  assert.deepEqual(fixture.frame.damage[0].elements, ['fire']);
  assert.equal(fixture.frame.damage[0].breakedHit, true);
  assert.equal(fixture.frame.damage[0].multiHit, undefined);
  assert.equal(uint64(fixture.group.breakedHitCount), '1');
  assert.equal(fixture.group.multiHitCount, null);
  assert.equal(uint64(fixture.group.elementCounts.fire), '1');
  assert.equal(fixture.frame.entities[0].breakGauge.current, 125.5);
  assert.equal(fixture.frame.entities[0].rawName, fixture.frame.entities[1].rawName);
  assert.notDeepEqual(fixture.frame.entities[0].key, fixture.frame.entities[1].key);
  assert.equal(fixture.frame.entities[1].breakGauge.maximum, null);
  const worker = { protocolVersion: 1, messageId: '1', requestId: 'a'.repeat(32), kind: 'event',
    runId: 'b'.repeat(32), bindingRevision: '1', fencingToken: '0', payload: { combat: {
      schemaVersion: 1, interestId: 'c'.repeat(32), localEntity: null, frame: fixture.frame } } };
  valid(workerSchema, worker);
  const invalid = structuredClone(worker); invalid.payload.combat.frame.damage[0].amount = 18446744073709551615;
  assert.equal(workerSchema(invalid), false);
  const legacyGroup = structuredClone(fixture.group);
  for (const key of ['powerfulHitCount', 'rapidHitCount', 'doubleHitCount', 'minimumDamage', 'maximumDamage',
    'classificationCounts', 'criticalEligibleCount', 'mainHitEligibleCount', 'breakedHitCount', 'breakOccurredCount',
    'firstAttackCount', 'defaultAttackCount', 'multiHitCount', 'elementCounts']) delete legacyGroup[key];
  valid(groupSchema, legacyGroup);
  const legacyFrame = structuredClone(fixture.frame);
  for (const entity of legacyFrame.entities) delete entity.breakGauge;
  for (const hit of legacyFrame.damage) for (const key of ['powerfulHit', 'rapidHit', 'doubleHit', 'classification',
    'breakedHit', 'breakOccurred', 'firstAttack', 'defaultAttack', 'elements']) delete hit[key];
  valid(frameSchema, legacyFrame);
  const legacyWorker = structuredClone(worker);
  legacyWorker.payload.combat.frame = legacyFrame;
  valid(workerSchema, legacyWorker);
  const future = structuredClone(fixture.frame);
  future.entities[0].breakGauge.quality = 'future-quality';
  future.damage[0].classification = 'future-classification';
  future.damage[0].elements = ['fire', 'future-element'];
  valid(frameSchema, future);
  const futureWorker = structuredClone(worker);
  futureWorker.payload.combat.frame = future;
  valid(workerSchema, futureWorker);
  const invalidClassification = structuredClone(worker);
  invalidClassification.payload.combat.frame.damage[0].classification = true;
  assert.equal(workerSchema(invalidClassification), false);
  for (const elements of [[1], ['fire', 'fire'], Array.from({ length: 9 }, (_, index) => 'e' + index), 'fire']) {
    const invalidElements = structuredClone(worker);
    invalidElements.payload.combat.frame.damage[0].elements = elements;
    assert.equal(workerSchema(invalidElements), false, JSON.stringify(elements));
  }
  const invalidCounts = structuredClone(fixture.group); invalidCounts.elementCounts.fire = 1;
  assert.equal(groupSchema(invalidCounts), false);
});

test('map change segments keep ordinal, start reason and their own quality in the encounter contract', () => {
  const encounterSchema = ajv.compile({ $ref: 'urn:combat-detail:openapi#/components/schemas/CombatEncounter' });
  const segments = read('artifacts/generated/contracts/fixtures/csharp-combat-segments.v1.json');
  assert.deepEqual(segments.map(value => value.segment.ordinal), [1, 2, 3, 4]);
  assert.deepEqual(segments.map(value => value.segment.startReason), ['first-damage', 'map-change', 'map-change', 'map-change']);
  assert.deepEqual(segments.slice(0, 3).map(value => value.summary.boundaryReason), ['connection-changed', 'connection-changed', 'connection-changed']);
  for (const value of segments) {
    valid(encounterSchema, value);
    assert.equal(value.summary.quality, 'confirmed');
    assert.equal(value.summary.attributedDamage, '246');
    assert.equal(typeof value.summary.encounterDps, 'number');
  }
  const legacy = structuredClone(segments[0]); delete legacy.segment;
  valid(encounterSchema, legacy);
  const invalid = structuredClone(segments[1]); invalid.segment.ordinal = '2';
  assert.equal(encounterSchema(invalid), false);
});

test('manual split is a conditional idempotent combat-control command with an explicit inactive error', async () => {
  const openapi = read('artifacts/generated/contracts/openapi/v1.json');
  const operation = openapi.paths['/api/v1/combat/encounters/{id}/split'].post;
  assert.equal(operation.operationId, 'splitCombatEncounter');
  const headers = operation.parameters.filter(value => value.in === 'header').map(value => [value.name, value.required]);
  assert.deepEqual(new Map(headers).get('Idempotency-Key'), true);
  assert.deepEqual(new Map(headers).get('If-Match'), true);
  const codes = read('artifacts/generated/contracts/error-codes.v1.json');
  assert.deepEqual(codes.find(value => value.code === 'combat.segmentInactive'), { code: 'combat.segmentInactive', status: 409, retryable: false });
  const segments = read('artifacts/generated/contracts/fixtures/csharp-combat-segments.v1.json');
  const ended = structuredClone(segments[0]);
  ended.summary.boundaryReason = 'manual-split';
  const encounterSchema = ajv.compile({ $ref: 'urn:combat-detail:openapi#/components/schemas/CombatEncounter' });
  valid(encounterSchema, ended);
  const next = structuredClone(segments[1]); next.segment.startReason = 'manual-split';
  valid(encounterSchema, next);
  const client = new MabiToolClient('http://127.0.0.1:19998', () => 'fixture-token', async (url, options) => {
    assert.equal(url.pathname, '/api/v1/combat/encounters/enc-1/split');
    assert.equal(options.method, 'POST');
    const sent = new Headers(options.headers);
    assert.equal(sent.get('Idempotency-Key'), 'split-key-1');
    assert.equal(sent.get('If-Match'), '"combat-boundary-enc-1-4"');
    assert.deepEqual(JSON.parse(options.body), { observationId: 'obs-1' });
    return Response.json(ended);
  });
  const result = await client.splitCombatEncounter('enc-1', { observationId: 'obs-1' }, 'split-key-1', '"combat-boundary-enc-1-4"');
  assert.equal(result.summary.boundaryReason, 'manual-split');
  assert.equal(result.summary.final, true);
});

test('generated TypeScript query decodes combat detail without rounding values', async () => {
  const page = { encounterId: 'fixture', revision: '1', items: [{ index: '1', offsetMs: 1000,
    observedAt: fixture.frame.observedAt, kind: 'damage', damage: fixture.frame.damage[0], effect: null,
    gap: null, attribution: 'local', entity: null }], nextCursor: null, damageBuckets: [], effectCoverage: [], gaps: [], truncated: false };
  valid(ajv.compile({ $ref: 'urn:combat-detail:openapi#/components/schemas/CombatTimelinePage' }), page);
  const client = new MabiToolClient('http://127.0.0.1:19998', () => 'fixture-token', async (url, options) => {
    assert.equal(url.pathname, '/api/v1/combat/encounters/fixture/timeline');
    assert.equal(options.method, 'GET');
    return Response.json(page);
  });
  const result = await client.getCombatTimeline('fixture');
  assert.equal(result.items[0].damage.amount, '18446744073709551615');
  assert.equal(result.items[0].damage.doubleHit, undefined);
  assert.equal(result.items[0].damage.classification, 'direct');
  assert.deepEqual(result.items[0].damage.elements, ['fire']);
  assert.equal(result.items[0].damage.firstAttack, false);
});
