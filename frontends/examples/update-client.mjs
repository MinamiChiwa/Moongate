import { resumeOperation } from './operation-client.mjs';
export { resumeOperation as resumeUpdateOperation };

export async function saveUpdatePolicyExample(client, policy, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  let etag = null;
  await client.getUpdatePolicy(signal, metadata => { etag = metadata.etag; });
  if (!etag) throw new Error('后端更新策略缺少 ETag');
  return client.saveUpdatePolicy(policy, idempotencyKey, etag, signal);
}

// 用户发起检查后展示后端状态；下载与安装由各自的明确操作提交。
export async function runUpdateCheckExample(client, { idempotencyKey = crypto.randomUUID(), ...options } = {}) {
  const started = await client.createUpdateCheck({}, idempotencyKey, options.signal);
  const result = await resumeOperation(client, started.id, options);
  const state = await client.getUpdateState(options.signal);
  let plan = null, etag = null;
  if (state.available?.planId) {
    plan = await client.getUpdatePlan(state.available.planId, options.signal, metadata => { etag = metadata.etag; });
    if (!etag) throw new Error('后端更新计划缺少 ETag');
  }
  return { ...result, state, plan, etag };
}

export async function submitUpdateDownload(client, { plan, etag }, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  if (!plan?.id || !etag) throw new TypeError('需要已展示的更新计划和 ETag');
  return client.downloadUpdatePlan(plan.id, idempotencyKey, etag, signal);
}

export async function submitUpdateInstall(client, { plan, etag }, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  if (!plan?.id || !etag) throw new TypeError('需要已展示的更新计划和 ETag');
  return client.applyUpdatePlan(plan.id, idempotencyKey, etag, signal);
}

// connect 通过本机发现取得当前宿主；恢复始终查询原操作。
export async function reconnectUpdateOperation(connect, operationId, { onOperation = () => {},
  signal = AbortSignal.timeout(300000) } = {}) {
  signal = AbortSignal.any([signal, AbortSignal.timeout(300000)]);
  while (true) {
    signal.throwIfAborted();
    try {
      const client = await connect(signal);
      const system = await client.getSystemInfo(signal);
      const result = await resumeOperation(client, operationId, { signal, onOperation });
      return { ...result, state: await client.getUpdateState(signal), instanceId: system.instanceId };
    } catch (error) {
      signal.throwIfAborted();
      if (!(error instanceof TypeError) && !['storage.maintenanceRequired', 'events.resyncRequired'].includes(error.problem?.code)) throw error;
      await new Promise((resolve, reject) => {
        const cancelled = () => { clearTimeout(timer); reject(signal.reason); };
        const timer = setTimeout(() => { signal.removeEventListener('abort', cancelled); resolve(); }, 250);
        signal.addEventListener('abort', cancelled, { once: true });
      });
    }
  }
}
