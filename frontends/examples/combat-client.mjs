import { MabiToolApiError } from '../../artifacts/generated/sdk/typescript/compiled/client.js';

const terminal = state => ['succeeded', 'failed', 'cancelled', 'interrupted'].includes(state);

// onState 和 onEffects 可接入页面或浮窗；统计与倒计时来自后端查询。
export async function runCombatExample(client, { scenario = 'normal', intervalMs = 400, onState = () => {},
  onEffects = () => {}, signal = AbortSignal.timeout(30000), idempotencyKey = crypto.randomUUID() } = {}) {
  const capabilities = await client.getCombatCapabilities(signal);
  if (!capabilities.simulation) throw new Error('合成示例需要 --mock 后端');
  const binding = await client.getGameBinding(signal);
  const request = { gameSessionId: binding.binding.session.id, damageEnabled: true, selfEffectsEnabled: true,
    monsterEffectsEnabled: true, includeOwnedEntities: true, rollingWindowMs: 5000, idleTimeoutMs: 10000,
    replayScenarioId: scenario, replayIntervalMs: intervalMs };
  const operation = await client.startCombatObservation(request, idempotencyKey, signal);
  const filter = { topics: ['combat.summary.changed', 'combat.effects.changed', 'combat.encounter.closed', 'combat.observation.degraded'],
    resourceIds: [], lastEventId: null };
  let cursor = null, snapshot = null, revision = -1n, disconnected = false, reconnected = false, recoveries = 0;
  const revisions = [];
  async function refresh() {
    const value = await client.getCombatSnapshot(signal);
    cursor = value.eventCursor;
    if (value.data?.observation.id === operation.id) {
      snapshot = value;
      const next = BigInt(value.resourceRevision);
      if (next > revision) {
        revision = next; revisions.push(value.resourceRevision); onState(value);
        for (const entity of value.data.entities.filter(x => x.role === 'monster'))
          onEffects(await client.getCombatEntityEffects(entity.id, signal));
      }
    }
  }
  while (!reconnected || !snapshot?.data || snapshot.data.observation.state === 'observing') {
    try {
      let reconnect = false;
      for await (const message of client.streamEvents({ ...filter, lastEventId: cursor }, signal)) {
        if (message.event === 'control') {
          if (message.data.code !== 'events.resyncRequired') throw new Error(message.data.code);
          cursor = null; reconnect = true; break;
        }
        if (!disconnected && message.event === 'snapshot') {
          // 首次主动断开后重取快照；自动断线仍用最后已应用的游标续传。
          cursor = null;
          disconnected = true; reconnect = true; break;
        }
        if (message.event === 'snapshot') reconnected = true;
        if (message.event === 'snapshot' || message.data.data?.observationId === operation.id) await refresh();
        else cursor = message.id;
        if (reconnected && snapshot?.data && snapshot.data.observation.state !== 'observing') break;
      }
      if (reconnected && snapshot?.data && snapshot.data.observation.state !== 'observing') break;
      if (!reconnect) throw new TypeError('战斗事件连接已结束');
    } catch (error) {
      if (error instanceof MabiToolApiError && error.problem?.code === 'events.resyncRequired') cursor = null;
      else if (!(error instanceof TypeError) || signal.aborted) throw error;
      if (++recoveries > 3) throw new Error('战斗重同步次数已达上限');
      await new Promise(resolve => setTimeout(resolve, recoveries * 100));
    }
  }
  await refresh();
  const result = await client.getCombatObservation(operation.id, signal);
  if (!terminal(result.state)) throw new Error('战斗终态回执尚未提交');
  return { operation: result, snapshot, revisions, disconnected, reconnected, recoveries };
}
