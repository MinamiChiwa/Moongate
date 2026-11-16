import { MabiToolApiError } from '../../artifacts/generated/sdk/typescript/compiled/client.js';

class ReplayConnectionClosed extends Error {}

const terminal = state => ['succeeded', 'failed', 'cancelled', 'interrupted'].includes(state);

// UI 可将 onState 接入任意框架；业务执行与恢复由同一后端维护。
export async function runReplayExample(client, { scenario = 'normal', intervalMs = 500,
  onState = () => {}, signal = AbortSignal.timeout(30000), idempotencyKey = crypto.randomUUID() } = {}) {
  const catalog = await client.getReplayCatalog(signal);
  if (!catalog.enabled) throw new Error('请连接 --mock 后端');
  const operation = await client.startReplay({ scenarioId: scenario, intervalMs }, idempotencyKey, signal);
  const filter = { topics: ['replay.observation-changed', 'operation.changed'], resourceIds: [operation.id], lastEventId: null };
  let cursor = null, result = null, disconnected = false, recoveries = 0, lastStep = 0;
  const steps = [];
  const accept = (type, data) => {
    if (type === 'operation.changed' && terminal(data.state)) {
      if (data.result) accept('replay.observation-changed', data.result);
      result = data;
    }
    if (type === 'replay.observation-changed' && data.value.step > lastStep) {
      lastStep = data.value.step;
      steps.push(data.value.step);
      onState({ source: data.source, quality: data.quality, state: data.value });
    }
  };
  while (!result) {
    try {
      if (cursor === null) {
        const snapshot = await client.getEventSnapshot(filter, signal);
        for (const resource of snapshot.resources) accept(resource.type, resource.data);
        cursor = snapshot.eventCursor;
        // 查询权威 Operation 补齐已完成但投影尚在发布队列的状态。
        const current = await client.getOperation(operation.id, signal);
        if (terminal(current.state)) accept('operation.changed', current);
        if (result) break;
      }
      let reconnect = false;
      for await (const message of client.streamEvents({ ...filter, lastEventId: cursor }, signal)) {
        if (message.event === 'control') {
          if (message.data.code !== 'events.resyncRequired') throw new Error(message.data.code);
          if (++recoveries > 3) throw new Error('重同步次数已达上限');
          cursor = null; reconnect = true; break;
        }
        if (message.event === 'snapshot') {
          for (const resource of message.data.resources) accept(resource.type, resource.data);
        } else accept(message.event, message.data.data);
        // 先应用数据再推进游标；实际前端也应保持这个顺序。
        cursor = message.id;
        if (result) break;
        if (!disconnected && message.event === 'replay.observation-changed') {
          disconnected = true; reconnect = true; break;
        }
      }
      if (!result && !reconnect) throw new ReplayConnectionClosed('事件连接已结束');
    } catch (error) {
      if (error instanceof MabiToolApiError && error.problem?.code === 'events.resyncRequired') cursor = null;
      else if (!(error instanceof TypeError || error instanceof ReplayConnectionClosed) || signal.aborted) throw error;
      if (++recoveries > 3) throw new Error('重同步次数已达上限');
      await new Promise(resolve => setTimeout(resolve, 100 * recoveries));
    }
  }
  return { operation: result, steps, disconnected, recoveries };
}

