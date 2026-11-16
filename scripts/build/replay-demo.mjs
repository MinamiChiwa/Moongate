import { readFile } from 'node:fs/promises';
import { createConnection } from 'node:net';
import { pathToFileURL } from 'node:url';
import { MabiToolClient } from '../../artifacts/generated/sdk/typescript/compiled/client.js';
import { runReplayExample } from '../../frontends/examples/replay-client.mjs';

export function bootstrap(discovery, clientName = 'replay-example') {
  return new Promise((resolve, reject) => {
    const socket = createConnection('\\\\.\\pipe\\' + discovery.pipeName);
    let bytes = Buffer.alloc(0), completed = false;
    socket.setTimeout(5000, () => socket.destroy(new Error('本机握手超时')));
    socket.on('error', reject);
    socket.on('connect', () => {
      const body = Buffer.from(JSON.stringify({ protocolVersion: 1, clientName }));
      const length = Buffer.alloc(4); length.writeInt32LE(body.length);
      socket.write(Buffer.concat([length, body]));
    });
    socket.on('data', chunk => {
      bytes = Buffer.concat([bytes, chunk]);
      if (bytes.length > 16388 || bytes.length >= 4 && (bytes.readInt32LE() < 1 || bytes.readInt32LE() > 16384)) {
        socket.destroy(new Error('握手响应长度无效')); return;
      }
      if (bytes.length >= 4 && bytes.length >= bytes.readInt32LE() + 4) {
        try { completed = true; resolve(JSON.parse(bytes.subarray(4).toString('utf8'))); socket.end(); }
        catch (error) { socket.destroy(error); }
      }
    });
    socket.on('end', () => { if (!completed) reject(new Error('握手响应不完整')); });
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    if (!process.argv[2]) throw new Error('用法：node scripts/build/replay-demo.mjs <instance.json> [normal|partial|failure]');
    const discovery = JSON.parse(await readFile(process.argv[2], 'utf8'));
    const handshake = await bootstrap(discovery);
    const client = new MabiToolClient(handshake.apiBaseUri, () => handshake.grant.token);
    const result = await runReplayExample(client, { scenario: process.argv[3] ?? 'normal', onState: state => console.log(JSON.stringify(state)) });
    console.log(JSON.stringify({ operationId: result.operation.id, state: result.operation.state,
      problemCode: result.operation.problem?.code ?? null, disconnected: result.disconnected, recoveries: result.recoveries, steps: result.steps }));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
