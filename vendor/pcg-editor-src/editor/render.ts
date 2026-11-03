import { Camera2D } from './camera';
import { BackgroundImage } from '../core/model';
import { imageCornersWorld, worldToImagePx } from '../core/background';
import { Guide } from './guides';
import { SnapResult } from './snap';
import { EditorViewState } from './viewstate';
import {
  GroundSurface,
  Id,
  LAYER_OF,
  ProjectModel,
  SceneObject,
  areaPaths,
  objectVertices,
} from '../core/model';
import {
  Bounds,
  Paths,
  Ring,
  boundsOfPaths,
  boundsOfPoints,
  pathsArea,
  ringArea,
  ringCentroid,
  polylineLength,
} from '../core/polygon';
import { Vec2, deg, dist, mid } from '../core/vec';

/** 工具产生的临时预览几何（不进入 ProjectModel） */
export type Preview =
  | { kind: 'ring'; ring: Ring; label?: string; invalid?: boolean; ghost?: boolean }
  | { kind: 'polyline'; pts: Vec2[]; label?: string; width?: number }
  | { kind: 'handle'; p: Vec2; dir?: number; width?: number; label?: string }
  | { kind: 'text'; p: Vec2; text: string; tone?: 'info' | 'warn' };

export interface MeasureOverlay {
  pts: Vec2[];
  cursor?: Vec2;
  mode: 'distance' | 'area';
}

export interface RenderInput {
  ctx: CanvasRenderingContext2D;
  width: number;
  height: number;
  camera: Camera2D;
  model: ProjectModel;
  view: EditorViewState;
  previews: Preview[];
  snap: SnapResult | null;
  guides: Guide[];
  boxSelect: Bounds | null;
  measure: MeasureOverlay | null;
  hoverId?: Id;
  /** 已解码的背景图（可能为 null：还没加载完） */
  bgImage?: HTMLImageElement | null;
  /** 两点标定中的临时点 */
  calib?: { a?: Vec2; b?: Vec2 } | null;
}

const C = {
  bg: '#0e1014',
  gridMinor: 'rgba(255,255,255,0.038)',
  gridMajor: 'rgba(255,255,255,0.085)',
  axis: 'rgba(120,190,255,0.20)',
  siteLine: '#f2b544',
  siteFill: 'rgba(242,181,68,0.05)',
  parcelLine: '#6ea8fe',
  parcelFill: 'rgba(110,168,254,0.07)',
  buildingFill: 'rgba(176,196,220,0.24)',
  buildingLine: '#d7e0ec',
  genFill: 'rgba(140,160,190,0.10)',
  genLine: 'rgba(200,215,235,0.55)',
  noBuildFill: 'rgba(255,92,92,0.09)',
  noBuildLine: '#ff6b6b',
  roadFill: '#33383f',
  roadLine: 'rgba(255,255,255,0.22)',
  roadDash: 'rgba(255,255,255,0.55)',
  access: '#4ade80',
  hover: 'rgba(34,211,238,0.55)',
  sel: '#22d3ee',
  selFill: 'rgba(34,211,238,0.10)',
  locked: '#f59e0b',
  invalid: '#ff5c5c',
  guide: 'rgba(255,94,168,0.85)',
  snap: '#e879f9',
  measure: '#facc15',
  text: '#e6ebf2',
  textDim: 'rgba(230,235,242,0.62)',
  hintBg: 'rgba(14,16,20,0.86)',
};

/**
 * 地面的 2D 表现按表面类型分色。
 *
 * 都画成同一个绿色的话，平面图上"这儿是铺装、那儿是绿地"完全看不出来——
 * 生成完之后用户没法在 2D 里判断结果对不对，也就谈不上"2D = 授权"。
 * 停车额外加斜剖线：平面惯例，而且它和铺装都是灰的，只靠色差不够分。
 */
const SURFACE_2D: Record<GroundSurface, { fill: string; line: string; dash: number[]; hatch?: string; label: string }> = {
  paving: { fill: 'rgba(168,182,203,0.16)', line: 'rgba(186,200,220,0.55)', dash: [7, 5], label: 'rgba(196,208,226,0.85)' },
  lawn: { fill: 'rgba(74,222,128,0.13)', line: 'rgba(110,231,183,0.50)', dash: [7, 5], label: 'rgba(134,239,172,0.85)' },
  parking: {
    fill: 'rgba(96,110,130,0.30)',
    line: 'rgba(210,220,235,0.60)',
    dash: [],
    hatch: 'rgba(208,220,236,0.16)',
    label: 'rgba(222,231,244,0.9)',
  },
  water: { fill: 'rgba(56,189,248,0.16)', line: 'rgba(125,211,252,0.55)', dash: [7, 5], label: 'rgba(147,197,253,0.85)' },
  dirt: { fill: 'rgba(180,140,90,0.14)', line: 'rgba(196,154,104,0.50)', dash: [7, 5], label: 'rgba(210,170,120,0.85)' },
};

/** 层绘制顺序（PRD §38） */
const DRAW_ORDER: Record<string, number> = {  site: 0,
  ground: 1,
  parcels: 2,
  roads: 3,
  buildings: 4,
  landscape: 5,
  access: 6,
};

export function render(input: RenderInput) {
  const { ctx, width, height, camera, model, view } = input;
  ctx.save();
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = C.bg;
  ctx.fillRect(0, 0, width, height);

  // §38 图层顺序：Background → Grid → …
  if (model.background && model.background.visible) {
    drawBackground(ctx, camera, model.background, input.bgImage ?? null);
  }
  if (view.gridVisible) drawGrid(ctx, camera, width, height, view.gridSize);
  drawOrigin(ctx, camera);

  const objs = model.order
    .map((id) => model.objects[id])
    .filter((o): o is SceneObject => !!o && o.visible && view.layerVisibility[LAYER_OF[o.kind]])
    .sort((a, b) => (DRAW_ORDER[LAYER_OF[a.kind]] ?? 9) - (DRAW_ORDER[LAYER_OF[b.kind]] ?? 9));

  for (const o of objs) drawObject(ctx, camera, o);

  // hover 高亮
  if (input.hoverId && !view.selectionIds.includes(input.hoverId)) {
    const o = model.objects[input.hoverId];
    if (o) drawOutline(ctx, camera, o, C.hover, 1.5, [4, 3]);
  }

  // 选中
  for (const id of view.selectionIds) {
    const o = model.objects[id];
    if (!o) continue;
    drawSelection(ctx, camera, o);
  }

  // 顶点手柄（单选面状对象时）
  if (view.selectionIds.length === 1) {
    const o = model.objects[view.selectionIds[0]];
    if (o && !o.locked) drawVertexHandles(ctx, camera, o);
  }

  // 预览
  for (const p of input.previews) drawPreview(ctx, camera, p);

  // 参考线
  for (const g of input.guides) drawGuide(ctx, camera, g);

  // 框选
  if (input.boxSelect) drawBoxSelect(ctx, camera, input.boxSelect);

  // 测量
  if (input.measure) drawMeasure(ctx, camera, input.measure);

  // 标定参考线
  if (input.calib) {
    let pxDist: number | null = null;
    if (model.background && input.calib.a && input.calib.b) {
      const pa = worldToImagePx(model.background, input.calib.a);
      const pb = worldToImagePx(model.background, input.calib.b);
      pxDist = Math.hypot(pb.x - pa.x, pb.y - pa.y);
    }
    drawCalibration(ctx, camera, input.calib, pxDist);
  }

  // 捕捉标记
  if (input.snap && input.snap.type !== 'none') drawSnap(ctx, camera, input.snap);

  ctx.restore();
}

/* -------------------------------- 背景图 -------------------------------- */

function drawBackground(
  ctx: CanvasRenderingContext2D,
  cam: Camera2D,
  bg: BackgroundImage,
  img: HTMLImageElement | null,
) {
  const corners = imageCornersWorld(bg).map((p) => cam.worldToScreen(p));

  if (img && img.naturalWidth > 0) {
    ctx.save();
    ctx.globalAlpha = Math.max(0, Math.min(1, bg.opacity));
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    const mpp = bg.metersPerPixel;
    const s = cam.scale;
    const cos = Math.cos(bg.rotation);
    const sin = Math.sin(bg.rotation);
    const o = cam.worldToScreen(bg.origin);
    // (px,py) → screen：图片像素直接进画布坐标，避免逐点换算
    ctx.transform(mpp * s * cos, -mpp * s * sin, mpp * s * sin, mpp * s * cos, o.x, o.y);
    ctx.drawImage(img, 0, 0);
    ctx.restore();
  } else {
    ctx.save();
    ctx.fillStyle = 'rgba(255,255,255,0.03)';
    ctx.beginPath();
    corners.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }

  // 图片边框 + 状态
  ctx.save();
  ctx.beginPath();
  corners.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
  ctx.closePath();
  ctx.strokeStyle = bg.locked ? 'rgba(245,158,11,0.55)' : 'rgba(255,255,255,0.28)';
  ctx.lineWidth = 1;
  ctx.setLineDash([4, 4]);
  ctx.stroke();
  ctx.restore();

  const tag = bg.locked ? 'Background · Locked' : 'Background';
  drawLabel(ctx, cam, corners[0], tag, bg.locked ? 'rgba(245,158,11,0.85)' : 'rgba(255,255,255,0.45)', true, {
    x: 6,
    y: -14,
  });
}

function drawCalibration(
  ctx: CanvasRenderingContext2D,
  cam: Camera2D,
  calib: { a?: Vec2; b?: Vec2 },
  pxDist: number | null,
) {
  const a = calib.a;
  const b = calib.b;
  ctx.save();
  ctx.strokeStyle = C.measure;
  ctx.fillStyle = C.measure;
  ctx.lineWidth = 1.5;

  const dot = (p: Vec2, label: string) => {
    const s = cam.worldToScreen(p);
    ctx.beginPath();
    ctx.arc(s.x, s.y, 4.5, 0, Math.PI * 2);
    ctx.fillStyle = '#0e1014';
    ctx.fill();
    ctx.strokeStyle = C.measure;
    ctx.lineWidth = 2;
    ctx.stroke();
    ctx.font = '10px ui-monospace, SFMono-Regular, Menlo, monospace';
    ctx.fillStyle = C.measure;
    ctx.fillText(label, s.x + 8, s.y - 8);
  };

  if (a) dot(a, 'A');
  if (b) dot(b, 'B');
  if (a && b) {
    const sa = cam.worldToScreen(a);
    const sb = cam.worldToScreen(b);
    ctx.beginPath();
    ctx.moveTo(sa.x, sa.y);
    ctx.lineTo(sb.x, sb.y);
    ctx.setLineDash([7, 5]);
    ctx.stroke();
    const mid = { x: (sa.x + sb.x) / 2, y: (sa.y + sb.y) / 2 };
    const text =
      pxDist !== null
        ? `${Math.round(pxDist)} px  →  输入实际距离`
        : '输入实际距离';
    ctx.font = '11px ui-sans-serif, system-ui, sans-serif';
    const tw = ctx.measureText(text).width;
    ctx.fillStyle = C.hintBg;
    ctx.fillRect(mid.x - tw / 2 - 6, mid.y - 24, tw + 12, 18);
    ctx.fillStyle = C.measure;
    ctx.fillText(text, mid.x - tw / 2, mid.y - 11);
  }
  ctx.restore();
}

/* ---------------------------------- 网格 --------------------------------- */

function drawGrid(ctx: CanvasRenderingContext2D, cam: Camera2D, w: number, h: number, gridSize: number) {
  const base = Math.max(0.05, gridSize);
  // 自适应：保证相邻网格线 >= 9px
  let step = base;
  while (step * cam.scale < 9) step *= 2;
  while (step * cam.scale > 90) step /= 2;

  const b = cam.visibleBounds();
  const x0 = Math.floor(b.minX / step) * step;
  const y0 = Math.floor(b.minY / step) * step;

  ctx.lineWidth = 1;
  for (let x = x0; x <= b.maxX; x += step) {
    const sx = Math.round(cam.worldToScreen({ x, y: 0 }).x) + 0.5;
    const major = Math.abs(x / (step * 5) - Math.round(x / (step * 5))) < 1e-6;
    ctx.strokeStyle = major ? C.gridMajor : C.gridMinor;
    ctx.beginPath();
    ctx.moveTo(sx, 0);
    ctx.lineTo(sx, h);
    ctx.stroke();
  }
  for (let y = y0; y <= b.maxY; y += step) {
    const sy = Math.round(cam.worldToScreen({ x: 0, y }).y) + 0.5;
    const major = Math.abs(y / (step * 5) - Math.round(y / (step * 5))) < 1e-6;
    ctx.strokeStyle = major ? C.gridMajor : C.gridMinor;
    ctx.beginPath();
    ctx.moveTo(0, sy);
    ctx.lineTo(w, sy);
    ctx.stroke();
  }
}

function drawOrigin(ctx: CanvasRenderingContext2D, cam: Camera2D) {
  const o = cam.worldToScreen({ x: 0, y: 0 });
  ctx.strokeStyle = C.axis;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, o.y + 0.5);
  ctx.lineTo(cam.width, o.y + 0.5);
  ctx.moveTo(o.x + 0.5, 0);
  ctx.lineTo(o.x + 0.5, cam.height);
  ctx.stroke();
}

/* ---------------------------------- 对象 --------------------------------- */

function tracePaths(ctx: CanvasRenderingContext2D, cam: Camera2D, paths: Paths) {
  ctx.beginPath();
  for (const ring of paths) {
    if (ring.length < 2) continue;
    const s0 = cam.worldToScreen(ring[0]);
    ctx.moveTo(s0.x, s0.y);
    for (let i = 1; i < ring.length; i++) {
      const s = cam.worldToScreen(ring[i]);
      ctx.lineTo(s.x, s.y);
    }
    ctx.closePath();
  }
}

function tracePolyline(ctx: CanvasRenderingContext2D, cam: Camera2D, pts: Vec2[]) {
  ctx.beginPath();
  pts.forEach((p, i) => {
    const s = cam.worldToScreen(p);
    if (i === 0) ctx.moveTo(s.x, s.y);
    else ctx.lineTo(s.x, s.y);
  });
}

function drawObject(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject) {
  const isGen = o.origin === 'generated';
  ctx.save();
  ctx.lineJoin = 'round';

  switch (o.kind) {
    case 'site': {
      tracePaths(ctx, cam, o.boundary);
      ctx.fillStyle = C.siteFill;
      ctx.fill('evenodd');
      ctx.setLineDash([]);
      ctx.strokeStyle = C.siteLine;
      ctx.lineWidth = 2;
      ctx.stroke();
      break;
    }
    case 'ground': {
      const s = SURFACE_2D[o.surface] ?? SURFACE_2D.paving;
      tracePaths(ctx, cam, o.boundary);
      ctx.fillStyle = s.fill;
      ctx.fill('evenodd');
      ctx.setLineDash(s.dash);
      ctx.strokeStyle = s.line;
      ctx.lineWidth = 1;
      ctx.stroke();
      if (s.hatch) hatch(ctx, cam, o.boundary, s.hatch);
      /*
       * 只给"最大的那一环"打标签，位置取它的重心。
       * 铺装经常被切成七八个碎块，每块都写一遍"硬质铺装 320 ㎡"会糊满整个画面。
       */
      const big = o.boundary.reduce<Ring | null>(
        (acc, r) => (!acc || Math.abs(ringArea(r)) > Math.abs(ringArea(acc)) ? r : acc),
        null,
      );
      /*
       * 面积写**整个对象的净面积**（扣洞），不是最大那一环的毛面积：
       * 铺装的外环常常把建筑抠成洞，按环算会报出 6,163 ㎡，而实际铺装只有 1,849 ㎡。
       * 绿地是"兜底填充"，自动生成时它铺满全场，标签只会和地块名压在一起，所以跳过；
       * 手画的绿地（Fixed）不是兜底，照常标。
       */
      const net = Math.abs(pathsArea(o.boundary));
      const show = !(o.origin === 'generated' && o.surface === 'lawn') && big && net >= (o.surface === 'parking' ? 120 : 400);
      if (show && big) {
        const c = ringCentroid(big);
        const sc = cam.worldToScreen(c);
        // 太小的时候屏幕上一片糊，不如不写
        if (cam.worldToScreen({ x: c.x + 8, y: c.y }).x - sc.x >= 26) {
          drawLabel(ctx, cam, c, `${o.name}  ${fmtArea(net)}`, s.label, false, { x: 4, y: 4 });
        }
      }
      break;
    }
    case 'parcel': {
      tracePaths(ctx, cam, o.boundary);
      ctx.fillStyle = isGen ? 'rgba(110,168,254,0.04)' : C.parcelFill;
      ctx.fill('evenodd');
      ctx.setLineDash(isGen ? [7, 5] : []);
      ctx.strokeStyle = isGen ? 'rgba(110,168,254,0.75)' : C.parcelLine;
      ctx.lineWidth = 1.6;
      ctx.stroke();
      // 地块名 + 面积：挂在外框左上角（总图的图签位置），不占场地中间
      const pb = boundsOfPaths(o.boundary);
      const area = o.boundary.reduce((s, r) => s + ringArea(r), 0);
      drawLabel(
        ctx,
        cam,
        { x: pb.minX, y: pb.maxY },
        `${o.name}  ${fmtArea(area)}`,
        'rgba(110,168,254,0.95)',
        true,
        { x: 4, y: 16 },
      );
      break;
    }
    case 'road': {
      drawRoad(ctx, cam, o);
      break;
    }
    case 'building': {
      tracePaths(ctx, cam, o.footprint);
      ctx.fillStyle = isGen ? C.genFill : C.buildingFill;
      ctx.fill('evenodd');
      ctx.setLineDash(isGen ? [6, 4] : []);
      ctx.strokeStyle = isGen ? C.genLine : C.buildingLine;
      ctx.lineWidth = 1.6;
      ctx.stroke();
      if (o.fixed && isGen) {
        // 已 Promote 的生成物用实线 + 角标区分
        ctx.setLineDash([]);
        drawLabel(ctx, cam, ringCentroid(o.footprint[0] ?? []), `${o.name} · fixed`, 'rgba(215,224,236,0.8)');
      }
      break;
    }
    case 'nobuild': {
      tracePaths(ctx, cam, o.boundary);
      ctx.fillStyle = C.noBuildFill;
      ctx.fill('evenodd');
      ctx.setLineDash([5, 4]);
      ctx.strokeStyle = C.noBuildLine;
      ctx.lineWidth = 1.4;
      ctx.stroke();
      hatch(ctx, cam, o.boundary, 'rgba(255,107,107,0.22)');
      drawLabel(ctx, cam, ringCentroid(o.boundary[0] ?? []), o.name, 'rgba(255,140,140,0.9)');
      break;
    }
    case 'access': {
      drawAccess(ctx, cam, o.pos, o.dir, o.width, C.access, o.locked);
      break;
    }
  }

  if (o.locked) drawLockBadge(ctx, cam, o);
  ctx.restore();
}

function drawRoad(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject & { kind: 'road' }) {
  if (o.centerline.length < 2) return;
  const half = (o.width / 2) * cam.scale;
  ctx.save();
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  ctx.setLineDash([]);

  // 1) 外缘（比路面宽 1.5px，形成路缘线）
  tracePolyline(ctx, cam, o.centerline);
  ctx.strokeStyle = o.origin === 'generated' ? 'rgba(255,255,255,0.22)' : C.roadLine;
  ctx.lineWidth = Math.max(4, half * 2 + 1.6);
  ctx.stroke();

  /*
   * 2) 路面
   * 生成的路早先用 rgba(45,50,57,.75) 画，和背景 #0e1014 几乎一个颜色，
   * 结果内部路网在平面图上"看不见"——只剩两道路缘虚线在飘。
   * 改成比铺装更深一档的沥青灰：背景 < 铺装 < 路面，路才立得起来。
   */
  tracePolyline(ctx, cam, o.centerline);
  ctx.strokeStyle = o.origin === 'generated' ? 'rgba(88,97,110,0.92)' : C.roadFill;
  ctx.lineWidth = Math.max(3, half * 2);
  ctx.stroke();

  // 3) 中心虚线
  if (o.width >= 5 && half * 2 > 12) {
    tracePolyline(ctx, cam, o.centerline);
    ctx.strokeStyle = o.origin === 'generated' ? 'rgba(255,255,255,0.30)' : C.roadDash;
    ctx.lineWidth = 1;
    ctx.setLineDash([9, 7]);
    ctx.stroke();
  }
  ctx.restore();

  const m = mid(o.centerline[0], o.centerline[o.centerline.length - 1]);
  drawLabel(ctx, cam, m, `${o.name} · ${o.width}m`, 'rgba(255,255,255,0.5)', true, {
    x: 10,
    y: -(half + 20),
  });
}

function drawAccess(
  ctx: CanvasRenderingContext2D,
  cam: Camera2D,
  pos: Vec2,
  dir: number,
  width: number,
  color: string,
  locked: boolean,
) {
  const s = cam.worldToScreen(pos);
  const halfPx = Math.max(6, (width / 2) * cam.scale);
  // 朝向：世界 (cos,sin) → 屏幕 (cos,-sin)
  const dx = Math.cos(dir);
  const dy = -Math.sin(dir);
  // 开口方向：与朝向垂直
  const px = -dy;
  const py = dx;

  ctx.save();
  // 门洞开口
  ctx.strokeStyle = color;
  ctx.lineWidth = 3.5;
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(s.x - px * halfPx, s.y - py * halfPx);
  ctx.lineTo(s.x + px * halfPx, s.y + py * halfPx);
  ctx.stroke();

  // 朝向箭头
  const L = 24;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.moveTo(s.x + dx * 4, s.y + dy * 4);
  ctx.lineTo(s.x + dx * L, s.y + dy * L);
  ctx.stroke();
  // 箭头头部
  const hx = s.x + dx * L;
  const hy = s.y + dy * L;
  ctx.beginPath();
  ctx.moveTo(hx, hy);
  ctx.lineTo(hx - dx * 6 + px * 4, hy - dy * 6 + py * 4);
  ctx.lineTo(hx - dx * 6 - px * 4, hy - dy * 6 - py * 4);
  ctx.closePath();
  ctx.fillStyle = color;
  ctx.fill();

  // 中心手柄
  ctx.beginPath();
  ctx.arc(s.x, s.y, 4.5, 0, Math.PI * 2);
  ctx.fillStyle = locked ? C.locked : '#0e1014';
  ctx.fill();
  ctx.strokeStyle = color;
  ctx.lineWidth = 2;
  ctx.stroke();
  ctx.restore();
}

function drawOutline(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject, color: string, lw: number, dash: number[] = []) {
  const paths = areaPaths(o);
  ctx.save();
  ctx.setLineDash(dash);
  ctx.strokeStyle = color;
  ctx.lineWidth = lw;
  if (paths) tracePaths(ctx, cam, paths);
  else if (o.kind === 'road') tracePolyline(ctx, cam, o.centerline);
  else if (o.kind === 'access') {
    const s = cam.worldToScreen(o.pos);
    ctx.beginPath();
    ctx.arc(s.x, s.y, 9, 0, Math.PI * 2);
  }
  ctx.stroke();
  ctx.restore();
}

function drawSelection(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject) {
  const paths = areaPaths(o);
  ctx.save();
  if (paths) {
    tracePaths(ctx, cam, paths);
    ctx.fillStyle = C.selFill;
    ctx.fill('evenodd');
  }
  ctx.shadowColor = 'rgba(34,211,238,0.55)';
  ctx.shadowBlur = 10;
  drawOutline(ctx, cam, o, C.sel, 2);
  ctx.shadowBlur = 0;
  ctx.restore();

  // 选中对象名称（已有静态标签的类型不再重复绘制）
  const hasStaticLabel = o.kind === 'parcel' || o.kind === 'nobuild' || o.kind === 'road';
  const anchor = paths ? ringCentroid(paths[0] ?? []) : null;
  if (anchor && !hasStaticLabel) {
    drawLabel(ctx, cam, anchor, o.name, 'rgba(34,211,238,0.95)', true, { x: 10, y: 16 });
  }
}

function drawVertexHandles(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject) {
  const paths = areaPaths(o);
  let pts: Vec2[] = [];
  if (paths) pts = paths.flat();
  else if (o.kind === 'road') pts = o.centerline;
  else if (o.kind === 'access') pts = [o.pos];
  if (pts.length > 400) return;

  ctx.save();
  for (const p of pts) {
    const s = cam.worldToScreen(p);
    ctx.fillStyle = '#0e1014';
    ctx.strokeStyle = C.sel;
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.rect(s.x - 3.5, s.y - 3.5, 7, 7);
    ctx.fill();
    ctx.stroke();
  }
  ctx.restore();
}

function drawLockBadge(ctx: CanvasRenderingContext2D, cam: Camera2D, o: SceneObject) {
  const v = objectVertices(o)[0];
  if (!v) return;
  const s = cam.worldToScreen(v);
  ctx.save();
  ctx.fillStyle = C.locked;
  ctx.beginPath();
  ctx.arc(s.x + 9, s.y - 9, 4, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
}

/* --------------------------------- 预览 --------------------------------- */

function drawPreview(ctx: CanvasRenderingContext2D, cam: Camera2D, p: Preview) {
  ctx.save();
  switch (p.kind) {
    case 'ring': {
      tracePaths(ctx, cam, [p.ring]);
      const col = p.invalid ? C.invalid : p.ghost ? 'rgba(34,211,238,0.5)' : C.sel;
      ctx.fillStyle = p.invalid ? 'rgba(255,92,92,0.10)' : 'rgba(34,211,238,0.08)';
      ctx.fill();
      ctx.setLineDash(p.ghost ? [5, 4] : []);
      ctx.strokeStyle = col;
      ctx.lineWidth = 1.6;
      ctx.stroke();
      if (p.label) {
        const b = boundsOfPoints(p.ring);
        drawLabel(ctx, cam, { x: (b.minX + b.maxX) / 2, y: (b.minY + b.maxY) / 2 }, p.label, col, true);
      }
      break;
    }
    case 'polyline': {
      if (p.width) {
        tracePolyline(ctx, cam, p.pts);
        ctx.strokeStyle = 'rgba(255,255,255,0.10)';
        ctx.lineWidth = Math.max(2, p.width * cam.scale);
        ctx.lineCap = 'round';
        ctx.stroke();
      }
      tracePolyline(ctx, cam, p.pts);
      ctx.strokeStyle = C.sel;
      ctx.lineWidth = 1.6;
      ctx.setLineDash([6, 4]);
      ctx.stroke();
      if (p.label) {
        const last = p.pts[p.pts.length - 1];
        drawLabel(ctx, cam, last, p.label, C.sel, true);
      }
      break;
    }
    case 'handle': {
      drawAccess(ctx, cam, p.p, p.dir ?? 0, p.width ?? 8, C.sel, false);
      if (p.label) drawLabel(ctx, cam, p.p, p.label, C.sel, true);
      break;
    }
    case 'text': {
      drawLabel(ctx, cam, p.p, p.text, p.tone === 'warn' ? C.invalid : C.text, true);
      break;
    }
  }
  ctx.restore();
}

function drawGuide(ctx: CanvasRenderingContext2D, cam: Camera2D, g: Guide) {
  ctx.save();
  ctx.strokeStyle = C.guide;
  ctx.lineWidth = 1;
  ctx.setLineDash([5, 4]);
  ctx.beginPath();
  if (g.axis === 'x') {
    const a = cam.worldToScreen({ x: g.value, y: g.from });
    const b = cam.worldToScreen({ x: g.value, y: g.to });
    ctx.moveTo(a.x + 0.5, a.y - 12);
    ctx.lineTo(b.x + 0.5, b.y + 12);
  } else {
    const a = cam.worldToScreen({ x: g.from, y: g.value });
    const b = cam.worldToScreen({ x: g.to, y: g.value });
    ctx.moveTo(a.x - 12, a.y + 0.5);
    ctx.lineTo(b.x + 12, b.y + 0.5);
  }
  ctx.stroke();
  ctx.restore();
}

function drawBoxSelect(ctx: CanvasRenderingContext2D, cam: Camera2D, b: Bounds) {
  const a = cam.worldToScreen({ x: b.minX, y: b.maxY });
  const c = cam.worldToScreen({ x: b.maxX, y: b.minY });
  ctx.save();
  ctx.fillStyle = 'rgba(34,211,238,0.08)';
  ctx.strokeStyle = 'rgba(34,211,238,0.7)';
  ctx.lineWidth = 1;
  ctx.setLineDash([4, 3]);
  ctx.fillRect(a.x, a.y, c.x - a.x, c.y - a.y);
  ctx.strokeRect(a.x + 0.5, a.y + 0.5, c.x - a.x, c.y - a.y);
  ctx.restore();
}

function drawMeasure(ctx: CanvasRenderingContext2D, cam: Camera2D, m: MeasureOverlay) {
  const pts = m.cursor ? [...m.pts, m.cursor] : m.pts;
  if (!pts.length) return;
  ctx.save();
  if (m.mode === 'area' && pts.length >= 3) {
    tracePaths(ctx, cam, [pts]);
    ctx.fillStyle = 'rgba(250,204,21,0.08)';
    ctx.fill();
  }
  if (pts.length >= 2) {
    tracePolyline(ctx, cam, pts);
    ctx.strokeStyle = C.measure;
    ctx.lineWidth = 1.4;
    ctx.setLineDash([6, 5]);
    ctx.stroke();
  }
  ctx.setLineDash([]);
  for (const p of m.pts) {
    const s = cam.worldToScreen(p);
    ctx.fillStyle = C.measure;
    ctx.beginPath();
    ctx.arc(s.x, s.y, 3, 0, Math.PI * 2);
    ctx.fill();
  }
  const last = pts[pts.length - 1];
  let text = '';
  if (m.mode === 'distance') {
    text = `${polylineLength(pts).toFixed(2)} m`;
    if (pts.length >= 2) {
      const a = pts[pts.length - 2];
      text += `   ∠${deg(Math.atan2(last.y - a.y, last.x - a.x)).toFixed(1)}°`;
    }
  } else {
    text = `${fmtArea(Math.abs(ringArea(pts)))}`;
    if (pts.length >= 3) text += `  ${polylineLength(pts).toFixed(2)} m`;
  }
  drawLabel(ctx, cam, last, text, C.measure, true);
  ctx.restore();
}

/* -------------------------------- 捕捉标记 ------------------------------- */

function drawSnap(ctx: CanvasRenderingContext2D, cam: Camera2D, snap: SnapResult) {
  const s = cam.worldToScreen(snap.point);
  ctx.save();
  ctx.strokeStyle = C.snap;
  ctx.fillStyle = 'rgba(14,16,20,0.9)';
  ctx.lineWidth = 1.5;
  const r = 5;

  switch (snap.type) {
    case 'vertex':
    case 'endpoint':
      ctx.beginPath();
      ctx.rect(s.x - r, s.y - r, r * 2, r * 2);
      ctx.fill();
      ctx.stroke();
      break;
    case 'midpoint':
      ctx.beginPath();
      ctx.moveTo(s.x, s.y - r - 1);
      ctx.lineTo(s.x + r + 1, s.y + r);
      ctx.lineTo(s.x - r - 1, s.y + r);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
      break;
    case 'intersection':
      ctx.beginPath();
      ctx.moveTo(s.x - r, s.y - r);
      ctx.lineTo(s.x + r, s.y + r);
      ctx.moveTo(s.x + r, s.y - r);
      ctx.lineTo(s.x - r, s.y + r);
      ctx.stroke();
      break;
    case 'edge':
      ctx.beginPath();
      ctx.moveTo(s.x - r, s.y);
      ctx.lineTo(s.x + r, s.y);
      ctx.stroke();
      break;
    case 'axis':
      ctx.beginPath();
      ctx.moveTo(s.x - r, s.y - r);
      ctx.lineTo(s.x + r, s.y + r);
      ctx.stroke();
      break;
    default:
      ctx.beginPath();
      ctx.arc(s.x, s.y, 3, 0, Math.PI * 2);
      ctx.stroke();
  }

  // 类型文字标签
  const text = snap.label;
  ctx.font = '10px ui-monospace, SFMono-Regular, Menlo, monospace';
  const tw = ctx.measureText(text).width;
  const bx = s.x + 12;
  const by = s.y + 12;
  ctx.fillStyle = C.hintBg;
  ctx.fillRect(bx - 4, by - 10, tw + 8, 16);
  ctx.fillStyle = C.snap;
  ctx.fillText(text, bx, by + 2);
  ctx.strokeStyle = C.snap;
  ctx.beginPath();
  ctx.moveTo(s.x, s.y);
  ctx.lineTo(bx - 4, by - 4);
  ctx.stroke();
  ctx.restore();
}

/* --------------------------------- 文字 --------------------------------- */

function drawLabel(
  ctx: CanvasRenderingContext2D,
  cam: Camera2D,
  world: Vec2,
  text: string,
  color: string,
  boxed = false,
  off: { x: number; y: number } = { x: 15, y: -13 },
) {
  if (!text) return;
  const s = cam.worldToScreen(world);
  ctx.save();
  ctx.font = '11px ui-sans-serif, system-ui, "Segoe UI", sans-serif';
  const tw = ctx.measureText(text).width;
  const x = s.x + off.x;
  const y = s.y + off.y;
  if (boxed) {
    ctx.fillStyle = C.hintBg;
    ctx.fillRect(x - 5, y - 9, tw + 10, 17);
  }
  ctx.fillStyle = color;
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x, y);
  ctx.restore();
}

function hatch(ctx: CanvasRenderingContext2D, cam: Camera2D, paths: Paths, color: string) {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const ring of paths) {
    for (const p of ring) {
      if (p.x < minX) minX = p.x;
      if (p.y < minY) minY = p.y;
      if (p.x > maxX) maxX = p.x;
      if (p.y > maxY) maxY = p.y;
    }
  }
  if (!Number.isFinite(minX)) return;
  const step = cam.pxToWorld(9);
  ctx.save();
  ctx.beginPath();
  for (const ring of paths) {
    if (ring.length < 3) continue;
    const s0 = cam.worldToScreen(ring[0]);
    ctx.moveTo(s0.x, s0.y);
    for (let i = 1; i < ring.length; i++) {
      const s = cam.worldToScreen(ring[i]);
      ctx.lineTo(s.x, s.y);
    }
    ctx.closePath();
  }
  ctx.clip('evenodd');
  ctx.strokeStyle = color;
  ctx.lineWidth = 1;
  ctx.beginPath();
  const span = maxY - minY;
  for (let d = minX - span; d < maxX; d += step * 2) {
    const a = cam.worldToScreen({ x: d, y: minY });
    const c = cam.worldToScreen({ x: d + span, y: maxY });
    ctx.moveTo(a.x, a.y);
    ctx.lineTo(c.x, c.y);
  }
  ctx.stroke();
  ctx.restore();
}

export function fmtArea(m2: number): string {
  if (m2 >= 10000) return `${(m2 / 10000).toFixed(2)} ha`;
  return `${Math.round(m2).toLocaleString('en-US')} ㎡`;
}

export function fmtLen(m: number): string {
  return `${m.toFixed(2)} m`;
}

/** 供状态栏 / 提示复用：屏幕空间世界距离 */
export const screenDist = (cam: Camera2D, a: Vec2, b: Vec2) => dist(a, b) * cam.scale;
