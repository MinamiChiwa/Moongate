import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { arch, release } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { bootstrap } from './replay-demo.mjs';
import { MabiToolClient } from '../../artifacts/generated/sdk/typescript/compiled/client.js';

const repository = fileURLToPath(new URL('../..', import.meta.url));
const hostDll = resolve(process.argv[2] ?? join(repository, 'artifacts/build/bin/MabiTool.Host/release/MabiTool.Host.dll'));
const exampleDll = resolve(process.argv[3] ?? join(repository, 'artifacts/build/frontend-sdk/bin/MabiTool.FrontendExamples/release/MabiTool.FrontendExamples.dll'));
const resultPath = resolve(process.argv[4] ?? join(repository, 'artifacts/test-results/frontend-sdk/workflow-comparison.json'));
const temporaryParent = join(repository, 'artifacts/tmp');
await mkdir(temporaryParent, { recursive: true });
const temporary = await mkdtemp(join(temporaryParent, 'frontend-smoke-'));
const environment = { ...process.env, MABITOOL_API_PORT: '0', MABITOOL_ALLOWED_ORIGINS: '' };
for (const name of ['MABITOOL_API_TOKEN', 'MABITOOL_RUNTIME_DIRECTORY', 'MABITOOL_BOOTSTRAP_DIRECTORY',
  'MABITOOL_DATA_ROOT', 'MABITOOL_UPDATE_CONFIGURATION']) delete environment[name];

function start(command, args, timeoutMs = 60000) {
  const process = spawn(command, args, { cwd: repository, env: environment, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '', errors = '', oversized = false;
  const append = (previous, chunk) => {
    if (Buffer.byteLength(previous) + Buffer.byteLength(chunk) > 131072) { oversized = true; process.kill(); return previous; }
    return previous + chunk;
  };
  process.stdout.setEncoding('utf8'); process.stderr.setEncoding('utf8');
  process.stdout.on('data', chunk => { output = append(output, chunk); });
  process.stderr.on('data', chunk => { errors = append(errors, chunk); });
  const timer = setTimeout(() => process.kill(), timeoutMs);
  const completion = new Promise((resolve, reject) => {
    process.once('error', reject);
    process.once('close', (code, signal) => { clearTimeout(timer); resolve({ code, signal }); });
  });
  return { process, completion, output: () => output, errors: () => errors, oversized: () => oversized };
}

async function finish(child) {
  const exit = await child.completion;
  if (child.oversized()) throw new Error('示例输出超过 128 KiB');
  if (exit.code !== 0) throw new Error(`示例退出 ${exit.code ?? exit.signal}: ${child.errors()}`);
  return child.output().trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
}

async function waitForDiscovery(host, path) {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (host.process.exitCode !== null || host.process.signalCode !== null) throw new Error(`模拟宿主退出: ${host.errors()}`);
    try {
      const discovery = JSON.parse(await readFile(path, 'utf8'));
      if (discovery.processId === host.process.pid) return discovery;
    } catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('模拟宿主发现超时');
}

function summarize(rows, scenario) {
  const operation = rows.at(-1);
  const observations = rows.filter(row => row.source && row.state);
  assert.equal(operation.state, scenario === 'failure' ? 'failed' : 'succeeded');
  assert.equal(operation.problemCode, scenario === 'failure' ? 'provider.unavailable' : null);
  assert.ok(observations.length > 0);
  for (const observation of observations) {
    assert.equal(observation.source.provider, 'replay-synthetic');
    assert.equal(observation.source.gameBuild, 'mock-v1');
    assert.equal(observation.state.combat.localEntity.rawInstanceId, '9007199254740993');
  }
  if (scenario !== 'failure') {
    assert.equal(operation.disconnected, true);
    assert.deepEqual(operation.steps, [1, 2, 3]);
  }
  if (scenario === 'partial') assert.ok(observations.some(row => row.quality === 'partial'));
  return { ...operation, qualities: observations.map(row => row.quality),
    localEntityId: observations[0].state.combat.localEntity.rawInstanceId, source: observations[0].source };
}

const host = start('dotnet', [hostDll, '--mock', '--portable-root', temporary, '--initialize-data'], 180000);
try {
  const discovery = join(temporary, 'runtime/instance.json');
  const info = await waitForDiscovery(host, discovery);
  const handshake = await bootstrap(info, 'frontend-comparison');
  const client = new MabiToolClient(handshake.apiBaseUri, () => handshake.grant.token);
  const system = await client.getSystemInfo();
  const capabilities = await client.getCapabilities();
  const scenarios = [];
  for (const scenario of ['normal', 'partial', 'failure']) {
    const dotnet = summarize(await finish(start('dotnet', [exampleDll, discovery, scenario])), scenario);
    const node = summarize(await finish(start(process.execPath, [join(repository, 'scripts/build/replay-demo.mjs'), discovery, scenario])), scenario);
    assert.notEqual(dotnet.operationId, node.operationId);
    assert.equal(dotnet.state, node.state);
    assert.equal(dotnet.problemCode, node.problemCode);
    assert.deepEqual(dotnet.steps, node.steps);
    assert.deepEqual(dotnet.source, node.source);
    scenarios.push({ scenario, dotnet, node });
    console.log(`${scenario}: .NET 与 TypeScript 通过，原操作及无损 ID 保留`);
  }
  await mkdir(dirname(resultPath), { recursive: true });
  await writeFile(resultPath, JSON.stringify({ environment: { os: release(), architecture: arch(), node: process.version,
    runtime: system.runtime, hostVersion: system.version }, capabilities, scenarios }, null, 2) + '\n');
} finally {
  if (host.process.exitCode === null && host.process.signalCode === null) host.process.kill();
  await host.completion;
  const owned = relative(temporaryParent, temporary);
  if (!owned.startsWith('frontend-smoke-') || owned.includes('/') || owned.includes('\\')) throw new Error('临时目录归属无效');
  await rm(temporary, { recursive: true, force: true });
}
