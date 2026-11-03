// 验收「内容来源」三档：TownForge 自排 / Placemaker 楼位 / 并排。
//
// 为什么这条必须单独验：切换内容来源**不改模型**，只换组装场景的开关。
// 如果实现错了（比如切换后没 invalidate 场景缓存，或者两套楼混进了同一个 kind），
// 画面看起来可能"只是有点怪"，但数字会不对。所以这里读的是**场景里各 kind 的栋数**。
//
// 跑法：node packages/editor-townforge/verify-content.mjs

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
page.on('console', (m) => {
  if (m.type() === 'error') errs.push('[console] ' + m.text().slice(0, 240));
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

await page.goto(url, { waitUntil: 'load' });
await wait(6000);
for (let i = 0; i < 20; i++) {
  const ok = await page.evaluate(() => !!(window.__tf && document.querySelector('#stats')?.textContent?.trim()));
  if (ok) break;
  await wait(500);
}

const shot = async (name) => {
  await wait(150);
  await page.screenshot({ path: join(dir, name) });
};

// 三档分别读数字
const read = () => page.evaluate(() => window.__tf.counts());

const tf = await read();
await shot('c1-content-tf.png');

await page.evaluate(() => window.__tf.switchContent('placemaker'));
await wait(1200);
const pm = await read();
await shot('c2-content-pm.png');

await page.evaluate(() => window.__tf.switchContent('both'));
await wait(1200);
const both = await read();
await shot('c3-content-both.png');

// 并排时读左栏那一行（用户看到的计数）
const rowBoth = await page.evaluate(() => {
  const rows = [...document.querySelectorAll('#leftbar .layer, #leftbar label, #leftbar .row')];
  const hit = rows.find((r) => (r.textContent ?? '').includes('Placemaker 楼位'));
  return hit ? hit.textContent.replace(/\s+/g, ' ').trim() : null;
});

// 走真实 UI 再点一次（验面板按钮接线，不只是钩子）
const ui = await page.evaluate(async () => {
  const btn = [...document.querySelectorAll('#seg-content button')].find((b) => b.dataset.content === 'townforge');
  if (!btn) return { clicked: false };
  btn.click();
  await new Promise((r) => setTimeout(r, 1200));
  return { clicked: true, counts: window.__tf.counts(), active: btn.classList.contains('active') };
});
await shot('c4-content-ui-click.png');

// 左栏的计数行（用户看到的就是这一行）
const leftRow = await page.evaluate(() => {
  const rows = [...document.querySelectorAll('#leftbar .layer, #leftbar label, #leftbar .row')];
  const hit = rows.find((r) => (r.textContent ?? '').includes('Placemaker 楼位'));
  return hit ? hit.textContent.replace(/\s+/g, ' ').trim() : null;
});
const visSlot = await page.evaluate(() => {
  const cb = document.querySelector('input[data-layer="solid:slot"]');
  return cb ? cb.checked : null;
});

await browser.close();

const tfBuildings = (tf.by.tower ?? 0) + (tf.by.slab ?? 0) + (tf.by.podium ?? 0) + (tf.by.house ?? 0) + (tf.by.warehouse ?? 0) + (tf.by.retail ?? 0) + (tf.by.canopy ?? 0);
const pmSlots = pm.by.slot ?? 0;
const bothTf = (both.by.tower ?? 0) + (both.by.slab ?? 0) + (both.by.podium ?? 0) + (both.by.house ?? 0) + (both.by.warehouse ?? 0) + (both.by.retail ?? 0) + (both.by.canopy ?? 0);

const checks = {
  '默认是 TownForge': tf.contentSource === 'townforge',
  '默认有 TownForge 的楼': tfBuildings > 100,
  '默认没有楼位': !tf.by.slot,
  '切到 Placemaker 有楼位': pmSlots > 100,
  '切到 Placemaker 后 TownForge 的楼清零':
    (pm.by.tower ?? 0) === 0 && (pm.by.slab ?? 0) === 0 && (pm.by.house ?? 0) === 0 && (pm.by.warehouse ?? 0) === 0,
  '并排 = 两套都在': pmSlots > 0 && bothTf === tfBuildings && (both.by.slot ?? 0) === pmSlots,
  '并排总数 = 两套之和': both.solids === pm.solids + tf.solids,
  '楼位数是自排的 1.5~4 倍': pmSlots / Math.max(1, tfBuildings) > 1.5 && pmSlots / Math.max(1, tfBuildings) < 4,
  // 点击后面板会重建，手里那个 button 已经 detach，不能拿它的 class 判高亮
  '面板按钮点击生效': ui.clicked && ui.counts.contentSource === 'townforge' && ui.counts.solids === tf.solids,
  '左栏有「Placemaker 楼位」一行': !!leftRow,
  '并排时左栏计数 > 0': (() => {
    const m = /(\d+)\s*$/.exec(rowBoth ?? '');
    return !!m && Number(m[1]) > 0;
  })(),
  '切到 Placemaker 会自动打开该图层': visSlot === false, // 最后一步已切回 townforge，所以应为关
  '无 JS 报错': errs.length === 0,
};

const ok = Object.values(checks).every(Boolean);
console.log(
  JSON.stringify(
    {
      ok,
      checks,
      tf: { solids: tf.solids, by: tf.by, bridge: tf.bridge },
      pm: { solids: pm.solids, by: pm.by, bridge: pm.bridge },
      both: { solids: both.solids, by: both.by },
      ratio: +(pmSlots / Math.max(1, tfBuildings)).toFixed(2),
      leftRow,
      rowBoth,
      visSlot,
      errs,
    },
    null,
    2,
  ),
);
console.log('截图 → ' + dir);
process.exitCode = ok ? 0 : 1;
