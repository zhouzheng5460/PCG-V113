// 编辑器验收（G1 Gate + 生成回路）。无头 Edge 在 file:// 下跑，不用任何服务。
//
// 为什么每条都要动真鼠标：**"能画"是产品的地基**，如果连一次拖拽都要靠内部 API 才能完成，
// 那就还是验证器，不是编辑器。所以这里的绘制 / 选择 / 拖动全部走 page.mouse。
//
// 跑法：node packages/editor-app/verify.mjs

import { copyFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const puppeteer = require('C:/Users/Administrator/WorkBuddy/2026-10-28-14-22-16/node_modules/puppeteer-core');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

const SRC = 'C:/Users/Administrator/WorkBuddy/城市工具最终版本/packages/editor-app/编辑器.html';
const dir = join(tmpdir(), 'town-editor');
mkdirSync(dir, { recursive: true });
const local = join(dir, 'editor.html');
copyFileSync(SRC, local);
const url = 'file:///' + local.replace(/\\/g, '/');

const S = (fn) => `(${fn.toString()})()`;
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

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
await page.goto(url, { waitUntil: 'load' });
await wait(900);

const shot = async (name) => {
  await wait(80);
  await page.screenshot({ path: join(dir, name) });
};

/**
 * 画布内的相对位置 → 页面坐标。
 *
 * 为什么不用世界坐标写死：相机取景会随"示例项目"的包围盒变化，写死 (20,-40) 这类点
 * 会落到视口外 —— 鼠标事件打在画布外面，什么都不会发生，而且不报错（第一版就栽在这）。
 * 用 0~1 的相对位置，跟缩放/平移彻底解耦。
 */
const at = async (fx, fy) =>
  page.evaluate((a, b) => {
    const cv = document.querySelector('#stage-canvas');
    const r = cv.getBoundingClientRect();
    return { x: r.left + r.width * a, y: r.top + r.height * b };
  }, fx, fy);

/** 屏幕上的一段像素距离换算成世界距离（用于面积期望值）。 */
const worldDelta = async (ax, ay, bx, by) =>
  page.evaluate((p) => ({ dx: p[0] / window.__CAM__.zoom, dy: p[1] / window.__CAM__.zoom }), [bx - ax, by - ay]);

const drag = async (a, b) => {
  await page.mouse.move(a.x, a.y);
  await page.mouse.down();
  await page.mouse.move((a.x + b.x) / 2, (a.y + b.y) / 2, { steps: 4 });
  await page.mouse.move(b.x, b.y, { steps: 4 });
  await page.mouse.up();
  await wait(120);
};

const clickAt = async (p) => {
  await page.mouse.click(p.x, p.y);
  await wait(120);
};

const state = () =>
  page.evaluate(() => {
    const st = window.__STORE__.state;
    const kinds = {};
    for (const id of st.model.order) {
      const o = st.model.objects[id];
      if (!o) continue;
      kinds[o.kind] = (kinds[o.kind] ?? 0) + 1;
    }
    return {
      objects: st.model.order.length,
      kinds,
      selected: st.view.selectionIds.length,
      tool: st.view.activeTool,
      drawKind: st.view.drawKind,
      past: st.past.length,
      future: st.future.length,
      toast: st.toast,
      gen: st.genStats,
    };
  });

// ── 0) 骨架
const boot = await page.evaluate(() => ({
  tools: document.querySelectorAll('.toolbar .tool').length,
  minis: document.querySelectorAll('.toolbar .mini').length,
  layers: document.querySelectorAll('#layers .layer').length,
  status: document.querySelectorAll('#statusbar span').length,
  canvas: !!document.querySelector('#stage-canvas'),
  name: (document.querySelector('#proj-name') || {}).value,
  objects: window.__STORE__.state.model.order.length,
  fatal: document.querySelector('.stage-3d-note') ? 'note-ok' : 'note-missing',
}));
await shot('a-boot-demo.png');

const initial = await state();

// ── 1) 画一个地块（矩形工具 + 真鼠标拖拽）
// 先定语义（地块），再换几何工具（矩形）。
// 顺序反了就会出错：点语义按钮会同时把工具切成它默认的几何工具（地块 → 多边形），
// 于是"按 R 切矩形"被覆盖，拖拽什么也不会发生。
await page.click('.toolbar .sem[data-kind="parcel"]');
await wait(80);
await page.keyboard.press('r');
await wait(80);
const toolRect = (await state()).tool;
const canvasBox = await page.evaluate(() => {
  const cv = document.querySelector('#stage-canvas');
  const r = cv.getBoundingClientRect();
  return { w: Math.round(r.width), h: Math.round(r.height), left: Math.round(r.left), top: Math.round(r.top) };
});
const a1 = await at(0.16, 0.6);
const b1 = await at(0.42, 0.84);
await drag(a1, b1);
const afterDraw = await state();
const expectArea = await worldDelta(a1.x, a1.y, b1.x, b1.y);

// 新地块的面积：应当约等于"屏幕矩形换算回世界"的面积（网格吸附会带来 ±1 m 的误差）
const drawn = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const id = st.view.selectionIds[0];
  const o = st.model.objects[id];
  if (!o) return null;
  const p = o.boundary[0];
  let a = 0;
  for (let i = 0; i < p.length; i++) {
    const q = p[i];
    const r = p[(i + 1) % p.length];
    a += q.x * r.y - r.x * q.y;
  }
  return { kind: o.kind, area: Math.abs(a / 2), id };
});
await shot('b-draw-parcel.png');

// ── 2) 选中 + 拖动移动 + 撤销
// 画完必须切回选择工具（和 PCG 一样：绘制工具不会自己退出）。
// 不切的话，下一次拖动会再画一个矩形 —— 表现是"移动没反应"，而且会留下一个 0.1 m 的废对象。
await page.keyboard.press('v');
await wait(80);
const moveId = await page.evaluate(() => window.__STORE__.state.view.selectionIds[0]);
const beforeMove = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const o = st.model.objects[st.view.selectionIds[0]];
  return { x: o.boundary[0][0].x, y: o.boundary[0][0].y, past: st.past.length };
});
const center = await at(0.29, 0.72);
await drag(center, { x: center.x + 60, y: center.y });
const afterMove = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const id = st.view.selectionIds[0];
  const o = st.model.objects[id];
  return { x: o.boundary[0][0].x, y: o.boundary[0][0].y, past: st.past.length, toast: st.toast };
});
await page.keyboard.down('Control');
await page.keyboard.press('z');
await page.keyboard.up('Control');
await wait(120);
const afterUndo = await page.evaluate((id) => {
  const st = window.__STORE__.state;
  const o = st.model.objects[id];
  return { x: o ? o.boundary[0][0].x : null, toast: st.toast, objects: st.model.order.length };
}, moveId);

// ── 3) 生成（选中地块 → 顶栏"生成"）
const genBefore = await state();
await page.click('#btn-gen');
await wait(500);
const genAfter = await state();
const genDetail = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const parcels = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'parcel');
  const p = parcels[parcels.length - 1];
  const gen = p ? st.genStats[p.id] : null;
  const buildings = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'building' && o.generatedFrom === p.id);
  const roads = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'road' && o.generatedFrom === p.id);
  // 层高必须落在 2.5~9 m 之间（住宅 3.0 / 办公 4.0 / 商业 4.5 / 工业 8.0）。
  // 不能用"高度能被层数整除"——4.5 m 层高的商业楼 2 层是 9 m，9/2 不整除，会被误判成错。
  const floorsOk = buildings.every((b) => b.floors >= 1 && b.height > 0 && b.height / b.floors >= 2.5 && b.height / b.floors <= 9.0);
  const inParcel = buildings.length > 0;
  return { parcel: p?.name ?? null, gen, buildings: buildings.length, roads: roads.length, floorsOk, inParcel };
});
await shot('c-generated.png');

// ── 4) 换一版（Shift+点击 = 换 seed），必须换出不同的 seed
const seedBefore = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const p = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'parcel').pop();
  return p ? p.seed : null;
});
await page.keyboard.down('Shift');
await page.click('#btn-gen');
await page.keyboard.up('Shift');
await wait(500);
const seedAfter = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const p = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'parcel').pop();
  return { seed: p ? p.seed : null, buildings: st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'building' && o.generatedFrom === p.id).length };
});

// ── 5) 布尔：两个重叠矩形 → 并集（选中要靠 Shift 点击，走真交互）
await page.click('.toolbar .sem[data-kind="building"]');
await wait(60);
await drag(await at(0.56, 0.2), await at(0.7, 0.36));
await drag(await at(0.62, 0.26), await at(0.76, 0.42));
const twoB = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const ids = st.model.order.filter((id) => st.model.objects[id].kind === 'building' && st.model.objects[id].origin === 'user');
  const areaOf = (id) => {
    const p = st.model.objects[id].footprint[0];
    let a = 0;
    for (let i = 0; i < p.length; i++) { const q = p[i]; const r = p[(i + 1) % p.length]; a += q.x * r.y - r.x * q.y; }
    return Math.abs(a / 2);
  };
  return { ids, areas: ids.map(areaOf) };
});
// Shift 点第一栋，把第二栋也加进选中。
// 注意：**必须点"只属于第一栋"的地方**。点它自己的重心是不行的 ——
// 两栋重叠时重心可能落在第二栋里，而命中测试是"最上层优先"，于是点到的还是第二栋，
// 选中数不变，布尔就报"需要至少 2 个面对象"（第一版就是这么错的）。
const firstB = await at(0.58, 0.23);
await page.keyboard.press('v');
await wait(80);
await page.keyboard.down('Shift');
await page.mouse.click(firstB.x, firstB.y);
await page.keyboard.up('Shift');
await wait(120);
const selBeforeBool = (await state()).selected;
await page.click('.toolbar [data-bool="union"]');
await wait(200);
const boolRes = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const b = st.model.order.map((id) => st.model.objects[id]).filter((o) => o && o.kind === 'building' && o.origin === 'user');
  const areaOf = (o) => {
    const p = o.footprint[0];
    let a = 0;
    for (let i = 0; i < p.length; i++) { const q = p[i]; const r = p[(i + 1) % p.length]; a += q.x * r.y - r.x * q.y; }
    return Math.abs(a / 2);
  };
  return { count: b.length, area: b.length ? areaOf(b[0]) : 0, toast: st.toast };
});
await shot('d-boolean.png');

// ── 6) 偏移：内缩 3 m，面积必须变小
await page.click('.toolbar [data-offset="-3"]');
await wait(200);
const offsetRes = await page.evaluate(() => {
  const st = window.__STORE__.state;
  const id = st.view.selectionIds[0];
  const o = st.model.objects[id];
  const p = o.footprint[0];
  let a = 0;
  for (let i = 0; i < p.length; i++) { const q = p[i]; const r = p[(i + 1) % p.length]; a += q.x * r.y - r.x * q.y; }
  return { area: Math.abs(a / 2), toast: st.toast };
});
await shot('e-offset.png');

// ── 7) 导出 JSON（结构要能被读回来）
const exported = await page.evaluate(() => {
  const m = JSON.parse(window.__STORE__.exportJson());
  return { version: m.version, objects: Object.keys(m.objects).length, order: m.order.length, hasParcel: m.order.some((id) => m.objects[id].kind === 'parcel') };
});

// ── 8) 删除 → 撤销 → 重做（必须真的改变对象数）
const beforeDel = (await state()).objects;
await page.keyboard.press('Delete');
await wait(150);
const afterDel = (await state()).objects;
await page.keyboard.down('Control');
await page.keyboard.press('z');
await page.keyboard.up('Control');
await wait(150);
const afterUndoDel = (await state()).objects;
await page.keyboard.down('Control');
await page.keyboard.down('Shift');
await page.keyboard.press('z');
await page.keyboard.up('Shift');
await page.keyboard.up('Control');
await wait(150);
const afterRedoDel = (await state()).objects;

await shot('e-final.png');
await browser.close();

// 逐条列出，否则失败时只有一个 false，找起来要命
const checks = {
  '页面无报错': errs.length === 0,
  '画布存在': boot.canvas,
  '工具栏 12 项': boot.tools === 12,
  '动作按钮 7 个': boot.minis === 7,
  '图层 7 行': boot.layers === 7,
  '状态栏字段': boot.status > 6,
  '示例项目 7 对象': boot.objects === 7,
  '画布有尺寸': canvasBox.w > 600 && canvasBox.h > 500,
  'R 切到矩形': toolRect === 'rect',
  '拖动画出了地块': afterDraw.kinds.parcel === initial.kinds.parcel + 1,
  '画出来的是地块': drawn?.kind === 'parcel',
  '面积与屏幕矩形一致': drawn.area > expectArea.dx * expectArea.dy * 0.8 && drawn.area < expectArea.dx * expectArea.dy * 1.2,
  '拖动移动生效': Math.abs(afterMove.x - beforeMove.x) > 1,
  '移动写入历史': afterMove.past === beforeMove.past + 1,
  '撤销回到原位': Math.abs(afterUndo.x - beforeMove.x) < 0.01,
  '撤销不丢对象': afterUndo.objects === afterDraw.objects,
  // 注意 ?? 0：没有某类对象时 kinds 里**没有这个键**，`undefined > 0` 是 false，
  // 会得出"生成没出楼"的假结论（这条让人白查了十分钟）。
  '生成出楼': (genAfter.kinds.building ?? 0) > (genBefore.kinds.building ?? 0),
  '生成出街道': (genAfter.kinds.road ?? 0) > (genBefore.kinds.road ?? 0),
  '生成有统计': !!genDetail.gen,
  '统计数=实际楼数': genDetail.gen.buildings === genDetail.buildings,
  '统计有街道': genDetail.gen.streets >= 1,
  '层高合理': genDetail.floorsOk,
  '楼在地块内': genDetail.inParcel,
  '换一版改 seed': seedBefore !== seedAfter.seed,
  '换一版仍出楼': seedAfter.buildings > 0,
  'Shift 多选 2 个': selBeforeBool === 2,
  '并集合并成 1 个': boolRes.count === 1,
  '并集面积变大': boolRes.area > Math.max(...twoB.areas),
  '内缩面积变小': offsetRes.area < boolRes.area * 0.95,
  '导出 JSON 结构完整': exported.version === 1 && exported.order === exported.objects && exported.hasParcel,
  '删除减少 1 个': afterDel === beforeDel - 1,
  '撤销恢复删除': afterUndoDel === beforeDel,
  '重做再次删除': afterRedoDel === afterDel,
};
const ok = Object.values(checks).every(Boolean);

console.log(
  JSON.stringify(
    {
      ok,
      checks,
      boot,
      canvasBox,
      initial,
      toolRect,
      afterDraw,
      drawn,
      expectArea,
      move: { beforeMove, afterMove, afterUndo },
      gen: { genBefore, genAfter, genDetail },
      reseed: { seedBefore, seedAfter },
      twoB,
      selBeforeBool,
      boolRes,
      offsetRes,
      exported,
      undoRedo: { beforeDel, afterDel, afterUndoDel, afterRedoDel },
      errs,
    },
    null,
    2,
  ),
);
console.log('截图 → ' + dir);
process.exitCode = ok ? 0 : 1;
