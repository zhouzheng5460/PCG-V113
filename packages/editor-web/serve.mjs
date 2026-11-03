// 零依赖静态服务器：只为把 public/ 用 http 提供出来。
//
// 为什么不直接双击 index.html：ES module + fetch 在 file:// 下会被 CORS 拦掉，
// 报的是 "Failed to fetch bundle.json"，看起来像数据没打包，其实不是。
//
// 跑法：node packages/editor-web/serve.mjs [port]

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const rootDir = resolve(here, 'public');
const port = Number(process.argv[2] ?? 8765);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
};

const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url ?? '/', 'http://localhost');
    let p = decodeURIComponent(url.pathname);
    if (p.endsWith('/')) p += 'index.html';
    // 目录穿越防护：normalize 后必须仍在 public/ 内
    const full = resolve(join(rootDir, normalize(p)));
    if (!full.startsWith(rootDir)) {
      res.writeHead(403).end('forbidden');
      return;
    }
    const s = await stat(full);
    if (!s.isFile()) {
      res.writeHead(404).end('not found');
      return;
    }
    const buf = await readFile(full);
    res.writeHead(200, { 'content-type': MIME[extname(full)] ?? 'application/octet-stream', 'cache-control': 'no-cache' });
    res.end(buf);
  } catch {
    res.writeHead(404).end('not found');
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`规则验证器 → http://127.0.0.1:${port}/`);
  console.log('（Ctrl+C 结束）');
});
