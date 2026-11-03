// 验证「内联版」在没有 http 服务的情况下真能跑起来（file:// 直开）。
//
// 为什么必须验：ES module + fetch 在 file:// 下会被 CORS 拦掉，内联版绕开这两点的
// 手法（经典脚本 + 短路 fetch）一旦有拼错的语句，页面会"白屏但无报错"，肉眼很难判断。
// 所以这里抓 console/pageerror，并断言真实 DOM 数量。
//
// 跑法：node packages/editor-web/verify-standalone.mjs

import { copyFileSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const puppeteer = require('C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/puppeteer-core');

const SRC = 'C:/Users/Administrator/WorkBuddy/城市工具最终版本/packages/editor-web/规则验证器.html';
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

// 项目路径含中文，file:// 下 chromium 偶尔会 ERR_FILE_NOT_FOUND，先复制到纯 ASCII 路径
const dir = join(tmpdir(), 'town-standalone');
mkdirSync(dir, { recursive: true });
const local = join(dir, 'validator.html');
copyFileSync(SRC, local);
if (!existsSync(local)) throw new Error('复制失败');

const logs = [];
const errs = [];

const browser = await puppeteer.launch({
  executablePath: EDGE,
  headless: 'new',
  args: ['--allow-file-access-from-files', '--no-sandbox'],
});
const page = await browser.newPage();
await page.setViewport({ width: 1500, height: 950 });
page.on('console', (m) => logs.push(`[${m.type()}] ${m.text()}`));
page.on('pageerror', (e) => errs.push(String(e)));
page.on('requestfailed', (r) => errs.push(`REQFAIL ${r.url()} ${r.failure()?.errorText ?? ''}`));

await page.goto('file:///' + local.replace(/\\/g, '/'), { waitUntil: 'load' });
await new Promise((r) => setTimeout(r, 1200));

const snap = await page.evaluate(() => ({
  proto: location.protocol,
  list: document.querySelectorAll('#list button').length,
  rules: document.querySelectorAll('#panel .rule, #panel [data-rule]').length,
  panelText: (document.querySelector('#panel')?.innerText ?? '').slice(0, 400),
  canvasSvgs: document.querySelectorAll('#canvas svg').length,
  meta: document.querySelector('#meta')?.textContent ?? '',
  isoIdx: document.querySelectorAll('#iso svg').length,
}));

// 点第 3 个地块，确认交互仍然可用
let afterClick = null;
const btns = await page.$$('#list button');
if (btns.length > 2) {
  await btns[2].click();
  await new Promise((r) => setTimeout(r, 500));
  afterClick = await page.evaluate(() => ({
    rules: document.querySelectorAll('#panel .rule, #panel [data-rule]').length,
    canvasSvgs: document.querySelectorAll('#canvas svg').length,
    title: (document.querySelector('#panel h2, #panel .sect')?.textContent ?? '').slice(0, 60),
  }));
}

await page.screenshot({ path: join(dir, 'shot.png') });
await browser.close();

const ok = errs.length === 0 && snap.list > 40 && snap.rules >= 8 && snap.canvasSvgs >= 1;
console.log(JSON.stringify({ ok, snap, afterClick, errs, logsTail: logs.slice(-6) }, null, 2));
console.log('\n截图 → ' + join(dir, 'shot.png'));
process.exitCode = ok ? 0 : 1;
