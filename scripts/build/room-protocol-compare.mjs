import { createHash } from 'node:crypto';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';

const repository = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const reference = resolve(process.argv[2] ?? join(repository, '../MobiFolio-reference-MabiSync'));
const assembly = resolve(process.argv[3] ?? join(repository, 'artifacts/build/rooms-backend/bin/MabiTool.RoomServer/release/MabiTool.RoomServer.dll'));
const output = join(repository, 'artifacts/test-results/rooms');
await mkdir(output, { recursive: true });
const tests = join(reference, 'tests/server.test.mjs');
const source = await readFile(tests);
const sources = JSON.parse(await readFile(join(repository, 'scripts/maintenance/source-baselines.json'), 'utf8'));
const upstream = sources.sources.find(item => item.id === 'mabisync');
if (!upstream) throw new Error('MabiSync 来源登记缺失');
const hash = createHash('sha256').update(source).digest('hex');
const fixed = spawnSync('git', ['-C', reference, 'cat-file', 'blob', `${upstream.commit}:tests/server.test.mjs`],
  { windowsHide: true, maxBuffer: 1_048_576 });
if (fixed.status !== 0 || createHash('sha256').update(fixed.stdout).digest('hex') !== hash)
  throw new Error('协议测试与登记的 MabiSync 固定提交不一致');

async function run(name, parameters) {
  const child = spawn(process.execPath, [...parameters, '--test-reporter=tap', '--test', tests],
    { cwd: reference, windowsHide: true, env: { ...process.env, MABITOOL_ROOM_SERVER_DLL: assembly }, stdio: ['ignore', 'pipe', 'pipe'] });
  let stdout = '', stderr = '';
  child.stdout.on('data', bytes => { stdout += bytes.toString(); });
  child.stderr.on('data', bytes => { stderr += bytes.toString(); });
  const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  await writeFile(join(output, `${name}.tap`), stdout + stderr, 'utf8');
  const summary = stdout.split(/\r?\n/).filter(line => /^# (tests|pass|fail|cancelled|skipped|duration_ms) /.test(line));
  if (!summary.some(line => /^# tests [1-9]/.test(line))) throw new Error(name + ' 未产生有效测试计数');
  console.log(`${name}: ${summary.join('; ')}`);
  return { name, code, summary };
}
const results = [];
results.push(await run('reference-node', []));
results.push(await run('target-dotnet', ['--import', pathToFileURL(join(repository, 'tests/EndToEnd/Rooms/register-reference-loader.mjs')).href]));
await writeFile(join(output, 'protocol-comparison.json'), JSON.stringify({ schemaVersion: 1, createdAt: new Date().toISOString(),
  sourceRepository: upstream.repository, sourceCommit: upstream.commit, testSourceSha256: hash, assembly, results }, null, 2), 'utf8');
if (results.some(item => item.code !== 0)) process.exitCode = 1;
