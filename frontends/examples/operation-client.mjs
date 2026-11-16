import { MabiToolApiError } from '../../artifacts/generated/sdk/typescript/compiled/client.js';

const terminal = value => ['succeeded', 'failed', 'cancelled', 'interrupted'].includes(value.state);

// 重连查询同一 Operation；新宿主的地址和凭据由调用方重新握手后提供。
export async function resumeOperation(client, operationId, { onOperation = () => {}, disconnectOnce = false,
  signal = AbortSignal.timeout(60000) } = {}) {
  const filter = { topics: ['operation.changed'], resourceIds: [operationId], lastEventId: null };
  let operation = await client.getOperation(operationId, signal), cursor = null, disconnected = false;
  onOperation(operation);
  for (let attempt = 0; attempt < 4; attempt++) {
    const snapshot = await client.getEventSnapshot(filter, signal);
    cursor ??= snapshot.eventCursor;
    operation = await client.getOperation(operationId, signal);
    if (terminal(operation)) return { operation, disconnected };
    try {
      for await (const frame of client.streamEvents({ ...filter, lastEventId: cursor }, signal)) {
        if (frame.event === 'control') {
          if (frame.data.code !== 'events.resyncRequired') throw new Error(frame.data.code);
          cursor = null; break;
        }
        if (frame.event === 'operation.changed' && frame.data.resourceId === operationId) {
          const next = frame.data.data;
          if (BigInt(next.revision) > BigInt(operation.revision)) { operation = next; onOperation(operation); }
        } else if (frame.event === 'snapshot') {
          operation = await client.getOperation(operationId, signal); onOperation(operation);
        }
        cursor = frame.id ?? cursor;
        if (disconnectOnce && !disconnected) { disconnected = true; break; }
        if (terminal(operation)) return { operation, disconnected };
      }
    } catch (error) {
      signal.throwIfAborted();
      if (error instanceof MabiToolApiError && error.problem?.code === 'events.resyncRequired') cursor = null;
      else if (!(error instanceof TypeError)) throw error;
    }
  }
  operation = await client.getOperation(operationId, signal);
  if (!terminal(operation)) throw new Error('操作的事件连接恢复次数已用完');
  return { operation, disconnected };
}
