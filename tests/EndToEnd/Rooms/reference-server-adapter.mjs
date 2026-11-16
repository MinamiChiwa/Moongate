import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createInterface } from 'node:readline';

// 原协议测试通过真实 WebSocket 连接 C# 服务，保留其全部请求与断言。
export async function createRoomServer({ port = 3132, host = '127.0.0.1' } = {}) {
  const assembly = process.env.MABITOOL_ROOM_SERVER_DLL;
  if (!assembly) throw new Error('MABITOOL_ROOM_SERVER_DLL 未配置');
  const child = spawn(process.env.MABITOOL_DOTNET ?? 'dotnet', [assembly, '--host', host, '--port', String(port)],
    { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout });
  let errors = '';
  child.stderr.on('data', bytes => { if (errors.length < 65536) errors += bytes.toString(); });
  let timer;
  const started = await new Promise((resolve, reject) => {
    timer = setTimeout(() => { child.kill(); reject(new Error(`C# 房间服务启动超时：${errors}`)); }, 15000);
    child.once('error', reject);
    child.once('exit', code => reject(new Error(`C# 房间服务退出 ${code}：${errors}`)));
    lines.on('line', line => {
      try { const value = JSON.parse(line); if (value.url && value.serverEpoch) resolve(value); } catch {}
    });
  }).finally(() => clearTimeout(timer));
  return { ...started, async close() {
    lines.close();
    if (child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit'); child.kill(); await exited;
    }
  } };
}
