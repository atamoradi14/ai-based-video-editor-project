import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const bridge = path.join(path.dirname(fileURLToPath(import.meta.url)), 'server.mjs');

test('bridge refuses a public bind without a token', async () => {
  const child = spawn(process.execPath, [bridge], {
    env: { ...process.env, BRIDGE_BIND: '0.0.0.0', ENGINE_INTERNAL_TOKEN: '', BRIDGE_PORT: '0' },
  });
  const code = await new Promise(resolve => child.on('exit', resolve));
  assert.equal(code, 1);
});
