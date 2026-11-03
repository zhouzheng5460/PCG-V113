// 验收真实产品（vendor/townforge）：单文件在 file:// 下能不能跑起来、两个页签是否都出画面。
//
// 为什么必须无头跑：这个编辑器一开机就生成一整座城（张量场 → 流线 → 面 → 路网 → 地块内容），
// 任何一步抛错都会白屏，而白屏的原因只会出现在 console 里。截图 + 报错一起看才判得准。
//
// 跑法：node packages/editor-townforge/verify.mjs

import { copyFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const puppeteer = require('C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/puppeteer-core');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

const SRC = 'C:/Users/Administrator/WorkBuddy/城市工具最终版本/packages/editor-townforge/编辑器.html';
const dir = join(tmpdir(), 'townforge');
mkdirSync(dir, { recursive: true });
const local = join(dir, 'townforge.html');
copyFileSync(SRC, local);
const url = 'file:///' + local.replace(/\\/g, '/');

const errs = [];
const browser = await puppeteer.launch({
  executablePath: EDGE,
  headless: 'new',
  args: ['--no-sandbox', '--use-gl=swiftshader', '--enable-unsafe-swiftshader', '--allow-file-access-from-files'],
});
const page = await browser.newPage();
await page.setViewport({ width: 1500, height: 940, deviceScaleFactor: 1 });
page.on('pageerror', (e) => errs.push('pageerror: ' + String(e).slice(0, 300)));
page.on('requestfailed', (r) => errs.push(`requestfailed: ${r.url().split('/').pop()} ${r.failure()?.errorText ?? ''}`));
page.on('console', (m) => {
  if (m.type() === 'error') errs.push('[console] ' + m.text().slice(0, 240));
});

await page.goto(url, { waitUntil: 'load' });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
// 生成整座城要时间：先等 6 s，再按"统计文本非空"轮询，最多再等 10 s
await wait(6000);
for (let i = 0; i < 20; i++) {
  const t = await page.evaluate(() => document.querySelector('#stats')?.textContent ?? '');
  if (t.trim().length > 0) break;
  await wait(500);
}

const snap = await page.evaluate(() => {
  const q = (s) => document.querySelector(s);
  const left = q('#leftbar');
  const right = q('#rightbar');
  const canvas = q('#canvas2d');
  // 画布非空白检测：抽样 2000 个像素，看有多少和左上角底色不同
  let ink = 0;
  let sampled = 0;
  try {
    const ctx = canvas.getContext('2d');
    const w = canvas.width;
    const h = canvas.height;
    const d = ctx.getImageData(0, 0, w, h).data;
    const step = Math.max(1, Math.floor((w * h) / 2000));
    const bg = [d[0], d[1], d[2]];
    for (let i = 0; i < w * h; i += step) {
      const o = i * 4;
      if (Math.abs(d[o] - bg[0]) + Math.abs(d[o + 1] - bg[1]) + Math.abs(d[o + 2] - bg[2]) > 24) ink++;
      sampled++;
    }
  } catch (e) {
    return { error: String(e) };
  }
  return {
    canvas: canvas ? `${canvas.width}x${canvas.height}` : 'none',
    inkRatio: sampled ? +(ink / sampled).toFixed(3) : 0,
    leftSections: left ? left.querySelectorAll('h3, .group-title, details, .row, label').length : 0,
    leftText: (left?.innerText ?? '').slice(0, 400),
    rightRows: right ? right.querySelectorAll('input, select, button').length : 0,
    rightText: (right?.innerText ?? '').slice(0, 500),
    stats: (document.querySelector('#stats')?.innerText ?? '').slice(0, 300),
    hint: (document.querySelector('#hint')?.textContent ?? '').slice(0, 200),
    tabs: [...document.querySelectorAll('.tab')].map((b) => b.textContent),
    title: document.title,
  };
});
await page.screenshot({ path: join(dir, 'a-2d.png') });

// 切到三维预览
const three = await page.evaluate(async () => {
  document.querySelector('.tab[data-view="3d"]').click();
  await new Promise((r) => setTimeout(r, 2500));
  const host = document.querySelector('#view3d');
  const cv = host?.querySelector('canvas');
  return {
    canvas: cv ? `${cv.width}x${cv.height}` : 'none',
    children: host?.children.length ?? 0,
    visible: host ? getComputedStyle(host).display : 'none',
  };
});
await wait(1200);
await page.screenshot({ path: join(dir, 'b-3d.png') });

// 切回 2D，然后点"导出 GLB"——拦下 createObjectURL，只验 blob 出来了、有多大，不落盘
await page.evaluate(() => document.querySelector('.tab[data-view="2d"]').click());
await wait(600);
const glb = await page.evaluate(async () => {
  const sizes = [];
  const orig = URL.createObjectURL.bind(URL);
  URL.createObjectURL = (b) => {
    sizes.push(b.size);
    return orig(b);
  };
  document.querySelector('#btn-export').click();
  await new Promise((r) => setTimeout(r, 2500));
  URL.createObjectURL = orig;
  return { count: sizes.length, bytes: sizes[0] ?? 0 };
});
await wait(400);

await browser.close();

const checks = {
  '页面标题是 TownForge': /TownForge/.test(snap.title ?? ''),
  '两个页签存在': (snap.tabs ?? []).join('') === '平面编辑三维预览',
  '左栏有内容': snap.leftSections > 10,
  '右栏有控件': snap.rightRows > 10,
  '2D 画布有尺寸': snap.canvas !== 'none',
  '2D 画布画出了东西': snap.inkRatio > 0.02,
  '统计文本非空': (snap.stats ?? '').trim().length > 0,
  '三维预览挂载了画布': three.canvas !== 'none',
  '导出 GLB 产出了文件': glb.count > 0 && glb.bytes > 20000,
  '无 JS 报错': errs.length === 0,
};

const ok = Object.values(checks).every(Boolean);
console.log(JSON.stringify({ ok, checks, snap, three, glb, errs }, null, 2));
console.log('截图 → ' + dir);
process.exitCode = ok ? 0 : 1;
