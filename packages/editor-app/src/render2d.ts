// 2D 渲染。**只有绘制，没有状态**：所有交互状态由控制器写进 store / previews，这里照着画。
//
// 视觉语言照搬 PCG（`editor/render.ts` 的 C / SURFACE_2D / DRAW_ORDER）：
// 园区黄、地块蓝、建筑亮灰、道路深灰带虚线中心线、禁建红、生成物偏暗弱。
// 这套配色的目的只有一个：**一眼分得出"这是什么东西""是我画的还是机器画的"**。

import type { Camera2D } from './camera.ts';
import { LAYER_OF, areaPaths, type GroundSurface, type ProjectModel, type SceneObject } from './model.ts';
import { boundsOfPaths, distToPaths, pointInPaths, polylineLength, ringCenter, type Bounds, type Ring } from './polygon.ts';
import { strokeEnvelope } from './polygon.ts';
import { DEG, dist, len, sub, type Vec2 } from './vec.ts';
import type { EditorViewState } from './store.ts';
import type { Guide, SnapResult } from './snap.ts';

export type Preview =
  | { kind: 'ring'; ring: Ring; label?: string; invalid?: boolean; ghost?: boolean }
  | { kind: 'polyline'; pts: Vec2[]; label?: string; width?: number }
  | { kind: 'handle'; p: Vec2; dir?: number; width?: number; label?: string };

export interface MeasureOverlay {
  pts: Vec2[];
  cursor?: Vec2;
  mode: 'distance' | 'area';
}

export interface RenderInput {
  ctx: CanvasRenderingContext2D;
  width: number;
  height: number;
  cam: Camera2D;
  model: ProjectModel;
  view: EditorViewState;
  previews: Preview[];
  snap: SnapResult | null;
  guides: Guide[];
  measure: MeasureOverlay | null;
  bgImage?: HTMLImageElement | null;
}

const C = {
  bg: '#0e1014',
  gridMinor: 'rgba(255,255,255,0.035)',
  gridMajor: 'rgba(255,255,255,0.08)',
  axis: 'rgba(120,190,255,0.22)',
  siteLine: '#f2b544',
  siteFill: 'rgba(242,181,68,0.06)',
  parcelLine: '#6ea8fe',
  parcelFill: 'rgba(110,168,254,0.08)',
  buildingFill: 'rgba(176,196,220,0.24)',
  buildingLine: '#d7e0ec',
  genFill: 'rgba(201,180,137,0.32)',
  genLine: 'rgba(233,213,170,0.8)',
  noBuildFill: 'rgba(255,92,92,0.09)',
  noBuildLine: '#ff6b6b',
  roadFill: '#33383f',
  roadLine: 'rgba(255,255,255,0.20)',
  roadDash: 'rgba(255,255,255,0.5)',
  access: '#4ade80',
  hover: 'rgba(34,211,238,0.55)',
  sel: '#22d3ee',
  selFill: 'rgba(34,211,238,0.10)',
  locked: '#f59e0b',
  invalid: '#ff5c5c',
  guide: 'rgba(255,94,168,0.8)',
  snap: '#e879f9',
  measure: '#facc15',
  text: '#e6ebf2',
  textDim: 'rgba(230,235,242,0.6)',
};

const SURFACE_2D: Record<GroundSurface, { fill: string; line: string; dash: number[]; hatch?: string }> = {
  paving: { fill: 'rgba(168,182,203,0.16)', line: 'rgba(186,200,220,0.5)', dash: [7, 5] },
  lawn: { fill: 'rgba(74,222,128,0.13)', line: 'rgba(110,231,183,0.45)', dash: [7, 5] },
  parking: { fill: 'rgba(96,110,130,0.28)', line: 'rgba(210,220,235,0.55)', dash: [], hatch: 'rgba(208,220,236,0.16)' },
  water: { fill: 'rgba(56,189,248,0.16)', line: 'rgba(125,211,252,0.5)', dash: [7, 5] },
  dirt: { fill: 'rgba(180,140,90,0.14)', line: 'rgba(196,154,104,0.45)', dash: [7, 5] },
};

/** 图层绘制顺序：园区在下、建筑在上（PCG PRD §38）。 */
const DRAW_ORDER: Record<string, number> = {
  site: 0,
  ground: 1,
  parcels: 2,
  roads: 3,
  buildings: 4,
  landscape: 5,
  access: 6,
};

export function render2d(input: RenderInput): void {
  const { ctx, width, height, cam, model, view } = input;
  ctx.save();
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = C.bg;
  ctx.fillRect(0, 0, width, height);

  drawBackground(input);

  const order = [...model.order].sort((a, b) => {
    const oa = model.objects[a];
    const ob = model.objects[b];
    if (!oa || !ob) return 0;
    return (DRAW_ORDER[LAYER_OF[oa.kind]] ?? 0) - (DRAW_ORDER[LAYER_OF[ob.kind]] ?? 0);
  });

  const sel = new Set(view.selectionIds);
  for (const id of order) {
    const o = model.objects[id];
    if (!o || !o.visible) continue;
    const layer = LAYER_OF[o.kind];
    if (!view.layerVisibility[layer]) continue;
    drawObject(input, o, sel.has(id), view.hoverId === id);
  }

  // 单选时的顶点手柄：只有解锁的对象才画（锁了不给拖，画了就是骗人）
  if (view.selectionIds.length === 1) {
    const o = model.objects[view.selectionIds[0]];
    if (o && !o.locked && view.layerVisibility[LAYER_OF[o.kind]]) drawHandles(ctx, cam, o);
  }

  for (const p of input.previews) drawPreview(ctx, cam, p);
  if (input.measure) drawMeasure(ctx, cam, input.measure);
  if (input.guides.length) drawGuides(ctx, cam, input.guides);
  if (input.snap) drawSnap(ctx, cam, input.snap);
  if (view.boxSelect) drawBox(ctx, cam, view.boxSelect);

  ctx.restore();
}

function drawBackground(input: RenderInput): void {
  const { ctx, cam, view } = input;
  if (input.bgImage) {
    // 底图按世界坐标贴（导入时已经记下 origin / metersPerPixel），所以缩放时不会飘
    const img = input.bgImage;
    const tl = cam.toScreen({ x: 0, y: 0 });
    ctx.save();
    ctx.globalAlpha = 0.85;
    ctx.drawImage(img, tl.x, tl.y, img.width, img.height);
    ctx.restore();
  }
  if (!view.gridVisible) return;

  const b = cam.viewBounds();
  const s = view.gridSize > 0 ? view.gridSize : 1;
  // 太密就不画：1 px 间距以下全是糊的，还拖慢渲染
  const stepPx = cam.px(s);
  ctx.lineWidth = 1;
  if (stepPx >= 6) {
    ctx.strokeStyle = C.gridMinor;
    ctx.beginPath();
    for (let x = Math.floor(b.minX / s) * s; x <= b.maxX; x += s) {
      const sx = Math.round(cam.toScreen({ x, y: 0 }).x) + 0.5;
      ctx.moveTo(sx, 0);
      ctx.lineTo(sx, input.height);
    }
    for (let y = Math.floor(b.minY / s) * s; y <= b.maxY; y += s) {
      const sy = Math.round(cam.toScreen({ x: 0, y }).y) + 0.5;
      ctx.moveTo(0, sy);
      ctx.lineTo(input.width, sy);
    }
    ctx.stroke();
  }

  // 每 10 格一条粗线
  const major = s * 10;
  if (cam.px(major) >= 12) {
    ctx.strokeStyle = C.gridMajor;
    ctx.beginPath();
    for (let x = Math.floor(b.minX / major) * major; x <= b.maxX; x += major) {
      const sx = Math.round(cam.toScreen({ x, y: 0 }).x) + 0.5;
      ctx.moveTo(sx, 0);
      ctx.lineTo(sx, input.height);
    }
    for (let y = Math.floor(b.minY / major) * major; y <= b.maxY; y += major) {
      const sy = Math.round(cam.toScreen({ x: 0, y }).y) + 0.5;
      ctx.moveTo(0, sy);
      ctx.lineTo(input.width, sy);
    }
    ctx.stroke();
  }

  // 原点轴线：没有它，大图里完全不知道自己在哪
  ctx.strokeStyle = C.axis;
  ctx.beginPath();
  const o = cam.toScreen({ x: 0, y: 0 });
  ctx.moveTo(0, Math.round(o.y) + 0.5);
  ctx.lineTo(input.width, Math.round(o.y) + 0.5);
  ctx.moveTo(Math.round(o.x) + 0.5, 0);
  ctx.lineTo(Math.round(o.x) + 0.5, input.height);
  ctx.stroke();
}

function pathOf(ctx: CanvasRenderingContext2D, cam: Camera2D, ring: Ring): void {
  if (ring.length < 2) return;
  ctx.beginPath();
  const p0 = cam.toScreen(ring[0]);
  ctx.moveTo(p0.x, p0.y);
  for (let i = 1; i < ring.length; i++) {
    const p = cam.toScreen(ring[i]);
    ctx.lineTo(p.x, p.y);
  }
  ctx.closePath();
}

function drawObject(input: RenderInput, o: SceneObject, selected: boolean, hovered: boolean): void {
  const { ctx, cam, model } = input;

  if (o.kind === 'site' || o.kind === 'parcel' || o.kind === 'nobuild' || o.kind === 'ground' || o.kind === 'building') {
    const paths = areaPaths(o);
    if (!paths) return;
    for (const ring of paths) {
      pathOf(ctx, cam, ring);
      if (o.kind === 'building') {
        ctx.fillStyle = o.origin === 'generated' ? C.genFill : C.buildingFill;
        ctx.fill();
        ctx.strokeStyle = o.origin === 'generated' ? C.genLine : C.buildingLine;
        ctx.lineWidth = 1.2;
        ctx.stroke();
      } else if (o.kind === 'ground') {
        const st = SURFACE_2D[o.surface];
        ctx.fillStyle = st.fill;
        ctx.fill();
        ctx.setLineDash(st.dash);
        ctx.strokeStyle = st.line;
        ctx.lineWidth = 1;
        ctx.stroke();
        ctx.setLineDash([]);
        if (st.hatch) hatch(ctx, cam, ring, st.hatch);
      } else {
        const fill = o.kind === 'site' ? C.siteFill : o.kind === 'nobuild' ? C.noBuildFill : C.parcelFill;
        const line = o.kind === 'site' ? C.siteLine : o.kind === 'nobuild' ? C.noBuildLine : C.parcelLine;
        ctx.fillStyle = fill;
        ctx.fill();
        ctx.strokeStyle = o.locked ? C.locked : line;
        ctx.lineWidth = o.kind === 'site' ? 1.8 : 1.3;
        if (o.origin === 'generated') ctx.setLineDash([6, 4]);
        ctx.stroke();
        ctx.setLineDash([]);
      }
    }
    // 面对象标名字：认不出"哪块是地块 1"的话，属性面板就没法用
    if (o.kind !== 'building' && paths[0]?.length) {
      const c = cam.toScreen(ringCenter(paths[0]));
      label(ctx, c, o.name, C.textDim);
    }
  } else if (o.kind === 'road') {
    const env = strokeEnvelope(o.centerline, o.width);
    if (env.length >= 3) {
      pathOf(ctx, cam, env);
      ctx.fillStyle = C.roadFill;
      ctx.fill();
      ctx.strokeStyle = o.locked ? C.locked : C.roadLine;
      ctx.lineWidth = 1;
      ctx.stroke();
    }
    // 中心线虚线：路宽撑起来之后，没有中心线就看不出走向
    ctx.beginPath();
    o.centerline.forEach((p, i) => {
      const s = cam.toScreen(p);
      if (i === 0) ctx.moveTo(s.x, s.y);
      else ctx.lineTo(s.x, s.y);
    });
    ctx.setLineDash([6, 6]);
    ctx.strokeStyle = C.roadDash;
    ctx.lineWidth = 1;
    ctx.stroke();
    ctx.setLineDash([]);
    if (cam.px(o.width) > 14) {
      // 放在 1/3 处而不是中点：中点常常正好是出入口，两个标签会叠成一团
      const at = cam.toScreen(o.centerline[Math.floor(o.centerline.length / 3)]);
      label(ctx, { x: at.x, y: at.y - 11 }, `${o.name} · ${o.width} m`, C.textDim);
    }
  } else if (o.kind === 'access') {
    const p = cam.toScreen(o.pos);
    const dir = { x: Math.cos(o.dir), y: -Math.sin(o.dir) };
    const tip = { x: p.x + dir.x * 22, y: p.y + dir.y * 22 };
    ctx.strokeStyle = C.access;
    ctx.fillStyle = C.access;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.lineTo(tip.x, tip.y);
    ctx.stroke();
    // 箭头
    const n = { x: -dir.y, y: dir.x };
    ctx.beginPath();
    ctx.moveTo(tip.x, tip.y);
    ctx.lineTo(tip.x - dir.x * 7 + n.x * 4, tip.y - dir.y * 7 + n.y * 4);
    ctx.lineTo(tip.x - dir.x * 7 - n.x * 4, tip.y - dir.y * 7 - n.y * 4);
    ctx.closePath();
    ctx.fill();
    // 开口宽度标注
    const half = cam.px(o.width / 2);
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(p.x + n.x * half, p.y + n.y * half);
    ctx.lineTo(p.x - n.x * half, p.y - n.y * half);
    ctx.stroke();
    label(ctx, { x: p.x, y: p.y + 14 }, o.name, C.access);
  }

  if (selected) {
    strokeOutline(input, o, C.sel, 2.4);
  } else if (hovered) {
    strokeOutline(input, o, C.hover, 1.8);
  }
}

function strokeOutline(input: RenderInput, o: SceneObject, color: string, w: number): void {
  const { ctx, cam } = input;
  const paths = areaPaths(o);
  ctx.strokeStyle = color;
  ctx.lineWidth = w;
  if (paths) {
    for (const ring of paths) {
      pathOf(ctx, cam, ring);
      ctx.stroke();
    }
  } else if (o.kind === 'road') {
    const env = strokeEnvelope(o.centerline, o.width);
    if (env.length >= 3) {
      pathOf(ctx, cam, env);
      ctx.stroke();
    }
  } else if (o.kind === 'access') {
    const p = cam.toScreen(o.pos);
    ctx.beginPath();
    ctx.arc(p.x, p.y, 9, 0, Math.PI * 2);
    ctx.stroke();
  }
}

function drawHandles(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject): void {
  const pts: Vec2[] = [];
  const paths = areaPaths(o);
  if (paths) for (const r of paths) pts.push(...r);
  else if (o.kind === 'road') pts.push(...o.centerline);
  else if (o.kind === 'access') pts.push(o.pos);

  ctx.fillStyle = '#0e1014';
  ctx.strokeStyle = C.sel;
  ctx.lineWidth = 1.6;
  for (const p of pts) {
    const s = cam.toScreen(p);
    ctx.beginPath();
    ctx.rect(s.x - 3.5, s.y - 3.5, 7, 7);
    ctx.fill();
    ctx.stroke();
  }
}

function drawPreview(ctx: CanvasRenderingContext2D, cam: Camera2D, p: Preview): void {
  if (p.kind === 'ring') {
    pathOf(ctx, cam, p.ring);
    ctx.fillStyle = p.ghost ? 'rgba(34,211,238,0.05)' : 'rgba(34,211,238,0.12)';
    ctx.fill();
    ctx.setLineDash([5, 4]);
    ctx.strokeStyle = p.invalid ? C.invalid : C.sel;
    ctx.lineWidth = 1.5;
    ctx.stroke();
    ctx.setLineDash([]);
    if (p.label) {
      const c = cam.toScreen(ringCenter(p.ring));
      label(ctx, c, p.label, p.invalid ? C.invalid : C.sel);
    }
  } else if (p.kind === 'polyline') {
    ctx.beginPath();
    p.pts.forEach((pt, i) => {
      const s = cam.toScreen(pt);
      if (i === 0) ctx.moveTo(s.x, s.y);
      else ctx.lineTo(s.x, s.y);
    });
    ctx.strokeStyle = C.sel;
    ctx.lineWidth = p.width ? Math.max(1, cam.px(p.width)) : 1.5;
    ctx.setLineDash([5, 4]);
    ctx.stroke();
    ctx.setLineDash([]);
    if (p.label) {
      const last = p.pts[p.pts.length - 1];
      if (last) label(ctx, cam.toScreen(last), p.label, C.text);
    }
  } else {
    const s = cam.toScreen(p.p);
    ctx.strokeStyle = C.sel;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(s.x, s.y, 5, 0, Math.PI * 2);
    ctx.stroke();
    if (p.dir !== undefined) {
      ctx.beginPath();
      ctx.moveTo(s.x, s.y);
      ctx.lineTo(s.x + Math.cos(p.dir) * 22, s.y - Math.sin(p.dir) * 22);
      ctx.stroke();
    }
    if (p.label) label(ctx, { x: s.x, y: s.y - 12 }, p.label, C.text);
  }
}

function drawMeasure(ctx: CanvasRenderingContext2D, cam: Camera2D, m: MeasureOverlay): void {
  const pts = m.cursor ? [...m.pts, m.cursor] : m.pts;
  if (pts.length < 2) return;
  ctx.beginPath();
  pts.forEach((p, i) => {
    const s = cam.toScreen(p);
    if (i === 0) ctx.moveTo(s.x, s.y);
    else ctx.lineTo(s.x, s.y);
  });
  ctx.strokeStyle = C.measure;
  ctx.lineWidth = 1.5;
  ctx.setLineDash([4, 4]);
  ctx.stroke();
  ctx.setLineDash([]);

  for (const p of pts) {
    const s = cam.toScreen(p);
    ctx.fillStyle = C.measure;
    ctx.beginPath();
    ctx.arc(s.x, s.y, 3, 0, Math.PI * 2);
    ctx.fill();
  }

  const text =
    m.mode === 'distance'
      ? `${polylineLength(pts).toFixed(2)} m`
      : `${Math.abs(ringAreaSigned(pts)).toFixed(1)} m²`;
  const at = cam.toScreen(pts[Math.floor(pts.length / 2)]);
  label(ctx, { x: at.x, y: at.y - 12 }, text, C.measure);
}

function ringAreaSigned(pts: Vec2[]): number {
  let a = 0;
  for (let i = 0; i < pts.length; i++) {
    const p = pts[i];
    const q = pts[(i + 1) % pts.length];
    a += p.x * q.y - q.x * p.y;
  }
  return a / 2;
}

function drawGuides(ctx: CanvasRenderingContext2D, cam: Camera2D, guides: Guide[]): void {
  ctx.save();
  ctx.setLineDash([3, 4]);
  ctx.strokeStyle = C.guide;
  ctx.lineWidth = 1;
  for (const g of guides) {
    const a = cam.toScreen(g.a);
    const b = cam.toScreen(g.b);
    ctx.beginPath();
    ctx.moveTo(a.x, a.y);
    ctx.lineTo(b.x, b.y);
    ctx.stroke();
  }
  ctx.restore();
}

function drawSnap(ctx: CanvasRenderingContext2D, cam: Camera2D, snap: SnapResult): void {
  if (!snap.label) return;
  const s = cam.toScreen(snap.p);
  ctx.strokeStyle = C.snap;
  ctx.lineWidth = 1.4;
  ctx.beginPath();
  ctx.rect(s.x - 5, s.y - 5, 10, 10);
  ctx.stroke();
  label(ctx, { x: s.x + 9, y: s.y - 9 }, snap.label, C.snap);
}

function drawBox(ctx: CanvasRenderingContext2D, cam: Camera2D, b: Bounds): void {
  const a = cam.toScreen({ x: b.minX, y: b.minY });
  const c = cam.toScreen({ x: b.maxX, y: b.maxY });
  const x = Math.min(a.x, c.x);
  const y = Math.min(a.y, c.y);
  const w = Math.abs(c.x - a.x);
  const h = Math.abs(c.y - a.y);
  ctx.fillStyle = 'rgba(34,211,238,0.06)';
  ctx.fillRect(x, y, w, h);
  ctx.strokeStyle = C.sel;
  ctx.lineWidth = 1;
  ctx.setLineDash([4, 4]);
  ctx.strokeRect(x + 0.5, y + 0.5, w, h);
  ctx.setLineDash([]);
}

function hatch(ctx: CanvasRenderingContext2D, cam: Camera2D, ring: Ring, color: string): void {
  const b = boundsOfPaths([ring]);
  if (!boundsOk(b)) return;
  const step = Math.max(0.4, cam.meters(6));
  ctx.save();
  pathOf(ctx, cam, ring);
  ctx.clip();
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let k = b.minX - (b.maxY - b.minY); k < b.maxX; k += step) {
    const a = cam.toScreen({ x: k, y: b.minY });
    const c = cam.toScreen({ x: k + (b.maxY - b.minY), y: b.maxY });
    ctx.moveTo(a.x, a.y);
    ctx.lineTo(c.x, c.y);
  }
  ctx.stroke();
  ctx.restore();
}

const boundsOk = (b: Bounds): boolean => Number.isFinite(b.minX) && b.maxX > b.minX;

function label(ctx: CanvasRenderingContext2D, at: Vec2, text: string, color: string): void {
  if (!text) return;
  ctx.save();
  ctx.font = '11px "Segoe UI", "Microsoft YaHei", system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  const w = ctx.measureText(text).width;
  ctx.fillStyle = 'rgba(14,16,20,0.72)';
  ctx.fillRect(at.x - w / 2 - 4, at.y - 8, w + 8, 16);
  ctx.fillStyle = color;
  ctx.fillText(text, at.x, at.y);
  ctx.restore();
}

export { C as COLORS, SURFACE_2D, DRAW_ORDER };
export function fmtArea(m2: number): string {
  const v = Math.abs(m2);
  if (v >= 10000) return `${(v / 10000).toFixed(2)} 公顷`;
  return `${v.toFixed(0)} m²`;
}

export function fmtLen(m: number): string {
  return `${m.toFixed(2)} m`;
}

export const fmtAngle = (rad: number): string => `${(rad * DEG).toFixed(1)}°`;

/** 命中测试的公差与几何求交（控制器共用，避免两处各写一份）。 */
export const hitTolerance = (cam: Camera2D): number => cam.meters(6);

export { dist, len, sub, distToPaths, pointInPaths };
