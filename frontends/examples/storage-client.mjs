import { MabiToolApiError } from '../../artifacts/generated/sdk/typescript/compiled/client.js';
import { resumeOperation as resumeStorageOperation } from './operation-client.mjs';
export { resumeStorageOperation };

// 前端展示后端预览并提交实际 ETag；回收对象、保护条件和执行结果由后端决定。
export async function runStorageExample(client, { categories = ['cache'], apply = false, onPreview = () => {},
  signal = AbortSignal.timeout(30000), idempotencyKey = crypto.randomUUID() } = {}) {
  const usage = await client.getStorageUsage(signal);
  let etag = null;
  const plan = await client.previewStorageCleanup({ categories }, idempotencyKey + '-preview', signal,
    metadata => { etag = metadata.etag; });
  onPreview(plan);
  if (!apply) return { usage, plan, result: null };
  if (!etag) throw new Error('后端清理预览缺少 ETag');
  let result;
  for (let attempt = 0; ; attempt++) {
    try { result = await client.executeStorageCleanup(plan.id, idempotencyKey + '-execute', etag, signal); break; }
    catch (error) {
      if (!(error instanceof MabiToolApiError) || error.problem?.code !== 'resource.busy' || attempt >= 50) throw error;
      await new Promise(resolve => setTimeout(resolve, 100));
      signal.throwIfAborted();
    }
  }
  const recovered = await client.getStorageCleanupResult(plan.id, signal);
  if (recovered.revision !== result.revision || recovered.state !== result.state) throw new Error('清理回执修订不一致');
  return { usage, plan, result: recovered };
}

export async function runStorageBackupExample(client, { idempotencyKey = crypto.randomUUID(), ...options } = {}) {
  const started = await client.createStorageBackup({}, idempotencyKey, options.signal);
  const result = await resumeStorageOperation(client, started.id, options);
  return { ...result, backup: result.operation.state === 'succeeded' ? await client.getStorageBackup(result.operation.result.id, options.signal) : null };
}

export async function previewStorageMaintenance(client, kind, request, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  if (!['restore', 'migrate'].includes(kind)) throw new TypeError('维护类型须为 restore 或 migrate');
  let etag = null;
  const preview = kind === 'restore' ? client.previewStorageRestore.bind(client) : client.previewStorageMigration.bind(client);
  const plan = await preview(request, idempotencyKey, signal, metadata => { etag = metadata.etag; });
  if (!etag) throw new Error('后端维护预览缺少 ETag');
  return { plan, etag };
}

export async function submitStorageMaintenance(client, { plan, etag }, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  if (!['restore', 'migrate'].includes(plan.kind)) throw new TypeError('维护计划类型无效');
  const execute = plan.kind === 'restore' ? client.executeStorageRestore.bind(client) : client.executeStorageMigration.bind(client);
  return execute(plan.id, idempotencyKey, etag, signal);
}

export async function previewStorageModuleDeletion(client, moduleId, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  let etag = null;
  const plan = await client.previewStorageModuleDeletion({ moduleId }, idempotencyKey, signal,
    metadata => { etag = metadata.etag; });
  if (!etag) throw new Error('后端模块删除预览缺少 ETag');
  return { plan, etag };
}

export async function submitStorageModuleDeletion(client, { plan, etag }, { idempotencyKey = crypto.randomUUID(),
  signal = AbortSignal.timeout(30000) } = {}) {
  return client.executeStorageModuleDeletion(plan.id, idempotencyKey, etag, signal);
}
