// 验证三维场景页：file:// 直开、抓报错、切视图、点选、量高、**增量更新**、确定性、截图。
//
// 为什么要无头浏览器而不是"看着差不多"：WebGL 的错误（几何 NaN、材质 undefined、
// 三角化失败）不会写进 DOM，只会在 canvas 里安静地少画一块，只有截图能看出来。
//
// 这里的三组断言对应三份文档里的 Gate：
//   F2 §4  —— 等轴量高，与 `层数 × 层高` 误差 < 2%
//   F3 §4  —— 改一栋楼的层数只重建 1 个节点；选中零几何重建；同一方案两次打开指纹一致
//   E §4.3 —— 关一个图层只删那一层

import { copyFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const puppeteer = require('C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/puppeteer-core');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

const SRC = 'C:/Users/Administrator/WorkBuddy/城市工具最终版本/packages/view3d/三维场景.html';
const dir = join(tmpdir(), 'town-3d');
mkdirSync(dir, { recursive: true });
const local = join(dir, 'scene.html');
copyFileSync(SRC, local);

const errs = [];
const browser = await puppeteer.launch({
  executablePath: EDGE,
  headless: 'new',
  args: ['--no-sandbox', '--use-gl=swiftshader', '--enable-unsafe-swiftshader', '--allow-file-access-from-files'],
});
const page = await browser.newPage();
await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
page.on('pageerror', (e) => errs.push(String(e).slice(0, 300)));
page.on('console', (m) => {
  if (m.type() === 'error') errs.push('[console] ' + m.text().slice(0, 200));
});

const url = 'file:///' + local.replace(/\\/g, '/');
await page.goto(url, { waitUntil: 'load' });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
await wait(2500);

// 关键：这个页面只在"画面变了"时渲染。WebGL 画布在没有 preserveDrawingBuffer 时，
// 合成一次后缓冲区可能已被清空 —— 此时截图会拿到一片背景色，看起来像"没渲染"。
// 所以每次截图前强制重绘一帧。
const shot = async (name) => {
  await page.evaluate(() => window.__STAGE__?.renderNow?.());
  await wait(120);
  await page.screenshot({ path: join(dir, name) });
};

const snap = await page.evaluate(() => {
  const list = document.querySelectorAll('#list .item');
  const canvas = document.querySelector('#stage canvas');
  const T = window.__APP__.debugState();
  return {
    items: list.length,
    first: list[0]?.textContent?.trim().replace(/\s+/g, ' ') ?? '',
    hasMassTag: document.querySelectorAll('#list .tag').length,
    canvas: canvas ? canvas.width + 'x' + canvas.height : 'none',
    viewBtns: [...document.querySelectorAll('#views .vbtn')].map((b) => b.textContent),
    layerBoxes: document.querySelectorAll('#toggles input').length,
    nodes: T.nodes,
    buildings: T.buildings,
    info: (document.querySelector('#info')?.innerText ?? '').slice(0, 320),
    meta: document.querySelector('#meta')?.textContent ?? '',
    fatal: document.querySelector('.fatal')?.textContent?.slice(0, 300) ?? '',
  };
});
const digest0 = await page.evaluate(() => window.__APP__.keyDigest());
await shot('a-default.png');

// ── 1) 五个视图预设：四个正交 + 一个透视，并且按钮状态要跟着走
const presets = [];
for (const p of ['iso', 'top', 'front', 'right', 'persp']) {
  await page.click(`#views .vbtn[data-p="${p}"]`);
  await wait(200);
  const st = await page.evaluate(() => ({ ...window.__APP__.debugState(), on: document.querySelector('#views .vbtn.on')?.dataset.p }));
  presets.push({ p, preset: st.preset, ortho: st.ortho, on: st.on });
  await shot(`v-${p}.png`);
}
const presetOk = presets.every((x) => x.preset === x.p && x.on === x.p && x.ortho === (x.p !== 'persp'));

// ── 2) 点选楼块：去点真实屏幕坐标，不走内部接口；选中必须**零几何重建**
await page.evaluate(() => window.__APP__.setPreset('iso'));
await wait(200);
const pt = await page.evaluate(() => window.__APP__.screenOf(0));
let pick = null;
if (pt) {
  const statsBefore = await page.evaluate(() => ({ ...window.__REALIZER__.stats }));
  await page.mouse.click(pt.x, pt.y);
  await wait(300);
  pick = await page.evaluate(
    (before) => {
      const R = window.__REALIZER__;
      const sel = R.selected;
      const mesh = R.pickables().find((m) => m.userData.nodeId === sel);
      const s = R.stats;
      return {
        id: sel,
        statsBefore: before,
        statsAfter: { ...s },
        // 选中不能触发任何重建：update() 根本没被调用，计数必须一模一样
        zeroRebuild: s.built === before.built && s.reused === before.reused && s.removed === before.removed,
        emissive: mesh ? mesh.material.emissive.getHex() : null,
        edgesOn: window.__STAGE__.root.getObjectByName('realizer').children.some((c) => c.children?.some?.((k) => k.type === 'LineSegments' && k.visible)),
        panel: (document.querySelector('#picked')?.innerText ?? '').slice(0, 220),
        hasSlider: !!document.querySelector('#fl'),
        onBtn: document.querySelector('#views .vbtn.on')?.dataset.p,
      };
    },
    statsBefore,
  );
  await shot('e-pick.png');
}

// ── 3) 量高对账（F2 Gate）：几何 vs 层数×层高，屏幕 vs 几何，两条都要 < 2%
const meas = await page.evaluate(() => ({ ...window.__APP__.measure(), preset: window.__STAGE__.preset }));
const measureOk = !!meas && meas.floors > 0 && meas.px > 4 && meas.errData < 2 && meas.errPx < 2;

// ── 4) 编辑回路（F3 Gate）：拖层数滑块 → 只有 1 个节点重建
const slider = await page.evaluate(() => {
  const R = window.__REALIZER__;
  const before = { ...R.stats };
  const el = document.querySelector('#fl');
  const target = Number(el.max);
  el.value = String(target);
  el.dispatchEvent(new Event('input', { bubbles: true }));
  return {
    before,
    after: { ...R.stats },
    floors: Number(document.querySelector('#flv')?.textContent ?? 0),
    target,
    selectedStill: R.selected,
  };
});
const sliderOk = slider.after.built === 1 && slider.after.removed === 1 && slider.after.reused === slider.before.total - 1;
await shot('f-floors.png');

// 恢复自动值，几何要回到原样（也是"重建 1 个"）
const sliderReset = await page.evaluate(() => {
  document.querySelector('#flreset')?.click();
  return { ...window.__REALIZER__.stats, digest: window.__APP__.keyDigest() };
});

// ── 5) 关一个图层：只删那一层，别的全部复用
const layerOff = await page.evaluate(() => {
  const R = window.__REALIZER__;
  const before = { ...R.stats };
  const box = [...document.querySelectorAll('#toggles input')].find((i) => i.dataset.k === 'markings');
  box.checked = false;
  box.dispatchEvent(new Event('change', { bubbles: true }));
  return { before, after: { ...R.stats } };
});
const layerOn = await page.evaluate(() => {
  const box = [...document.querySelectorAll('#toggles input')].find((i) => i.dataset.k === 'markings');
  box.checked = true;
  box.dispatchEvent(new Event('change', { bubbles: true }));
  return { ...window.__REALIZER__.stats, digest: window.__APP__.keyDigest() };
});
const layerOk =
  layerOff.after.built === 0 &&
  layerOff.after.removed === 1 &&
  layerOff.after.reused === layerOff.before.total - 1 &&
  layerOn.built === 1 &&
  layerOn.removed === 0;

// ── 6) 换方案：选中态清空、不残留高亮
const btns = await page.$$('#list .item');
const targetIdx = Math.min(2, btns.length - 1);
if (targetIdx >= 0) {
  await btns[targetIdx].click();
  await wait(1500);
  await shot('d-other.png');
}
const after = await page.evaluate(() => ({
  info: (document.querySelector('#info')?.innerText ?? '').slice(0, 320),
  selected: window.__REALIZER__.selected,
  emissiveLeft: window.__REALIZER__.pickables().filter((m) => m.material.emissive?.getHex?.() === 0xffc83d).length,
  edgesVisible: window.__STAGE__.root
    .getObjectByName('realizer')
    .children.filter((c) => c.children?.some?.((k) => k.type === 'LineSegments' && k.visible)).length,
  fatal: document.querySelector('.fatal')?.textContent?.slice(0, 300) ?? '',
}));

// ── 7) 确定性：重新加载页面，同一份方案必须给出同一份指纹
await page.goto(url, { waitUntil: 'load' });
await wait(2200);
const digest1 = await page.evaluate(() => window.__APP__.keyDigest());

await browser.close();

const ok =
  errs.length === 0 &&
  snap.items > 50 &&
  snap.canvas !== 'none' &&
  !snap.fatal &&
  !after.fatal &&
  snap.viewBtns.length === 5 &&
  snap.layerBoxes === 6 &&
  snap.buildings > 0 &&
  presetOk &&
  !!pick &&
  !!pick.id &&
  pick.emissive === 0xffc83d &&
  pick.zeroRebuild &&
  pick.edgesOn &&
  pick.hasSlider &&
  pick.onBtn === 'iso' &&
  measureOk &&
  sliderOk &&
  layerOk &&
  after.selected === null &&
  after.emissiveLeft === 0 &&
  after.edgesVisible === 0 &&
  digest0.length > 100 &&
  digest0 === digest1;

console.log(
  JSON.stringify(
    {
      ok,
      snap,
      presets,
      presetOk,
      pick,
      meas,
      measureOk,
      slider,
      sliderOk,
      sliderReset,
      layerOff,
      layerOn,
      layerOk,
      after,
      determinism: { len: digest0.length, same: digest0 === digest1 },
      errs,
    },
    null,
    2,
  ),
);
console.log('截图 → ' + dir);
process.exitCode = ok ? 0 : 1;
