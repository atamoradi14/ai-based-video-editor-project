import http from 'node:http';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { agentTools } from '../ai-edit-studio/src/agent-tools.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const engineEntry = path.resolve(here, '../ai-edit-studio/server.mjs');
const token = process.env.ENGINE_INTERNAL_TOKEN || '';
const bind = process.env.BRIDGE_BIND || '127.0.0.1';
const port = Number(process.env.BRIDGE_PORT || 32147);
const enginePort = Number(process.env.GAME_TRAILER_STUDIO_PORT || 32146);
const allowed = new Set(agentTools.map(tool => tool.name));

if (bind !== '127.0.0.1' && bind !== 'localhost' && !token) {
  console.error('ENGINE_INTERNAL_TOKEN is required when BRIDGE_BIND is not loopback.');
  process.exit(1);
}

const engine = spawn(process.execPath, [engineEntry], {
  env: {
    ...process.env,
    GAME_TRAILER_STUDIO_PORT: String(enginePort),
    GAME_TRAILER_STUDIO_DATA_ROOT: process.env.GAME_TRAILER_STUDIO_DATA_ROOT || process.env.ENGINE_DATA_ROOT || '',
  },
  stdio: ['ignore', 'inherit', 'inherit'],
});

function stop() {
  engine.kill('SIGTERM');
  setTimeout(() => process.exit(0), 500);
}
process.on('SIGTERM', stop);
process.on('SIGINT', stop);

function send(res, status, body) {
  const raw = JSON.stringify(body);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(raw);
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url || '/', `http://127.0.0.1:${port}`);
    if (req.method === 'GET' && url.pathname === '/health') {
      return send(res, 200, { ok: true, service: 'ai-content-video-bridge', engine: `http://127.0.0.1:${enginePort}` });
    }
    if (token) {
      const header = String(req.headers.authorization || '');
      if (header !== `Bearer ${token}`) return send(res, 401, { ok: false, error: { code: 'unauthorized', message: 'Missing internal token.' } });
    }
    if (req.method === 'GET' && url.pathname === '/api/agent/tools') {
      const upstream = await fetch(`http://127.0.0.1:${enginePort}/api/agent/tools`);
      res.writeHead(upstream.status, { 'content-type': 'application/json; charset=utf-8' });
      return res.end(Buffer.from(await upstream.arrayBuffer()));
    }
    const match = url.pathname.match(/^\/api\/agent\/tools\/(studio_[a-z0-9_]+)$/);
    if (req.method === 'POST' && match) {
      if (!allowed.has(match[1])) return send(res, 403, { ok: false, error: { code: 'tool_not_allowed', message: match[1] } });
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const upstream = await fetch(`http://127.0.0.1:${enginePort}/api/agent/tools/${match[1]}`, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'x-studio-client': 'ai-content-bridge' },
        body: Buffer.concat(chunks),
      });
      res.writeHead(upstream.status, { 'content-type': 'application/json; charset=utf-8' });
      return res.end(Buffer.from(await upstream.arrayBuffer()));
    }
    return send(res, 404, { ok: false, error: { code: 'not_found', message: 'Unknown bridge route.' } });
  } catch (error) {
    send(res, 502, { ok: false, error: { code: 'bridge_error', message: error.message } });
  }
});

server.listen(port, bind, () => {
  console.log(`AI Content video bridge listening on ${bind}:${port}`);
});
