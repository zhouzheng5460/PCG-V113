import { camera } from './camera';
import { getImage } from './imageCache';
import { backgroundBounds } from '../core/background';
import { Guide, alignmentGuides, guideSnapDelta } from './guides';
import { MeasureOverlay, Preview, RenderInput, fmtArea, fmtLen, render } from './render';
import { SNAP_RADIUS_PX, SnapResult, findSnap } from './snap';
import { pickableObjects, selectionBounds, useEditor } from './store';
import {
  DrawKind,
  EditorViewState,
  TOOL_SHORTCUT,
} from './viewstate';
import {
  makeAccess,
  makeBuilding,
  makeGround,
  makeNoBuild,
  makeParcel,
  makeRoad,
  makeSite,
  addTo,
} from '../core/factory';
import { Id, LAYER_OF, ProjectModel, SceneObject, areaPaths, isAreaObj, objectBounds } from '../core/model';
import {
  Bounds,
  Paths,
  Ring,
  boundsOfPoints,
  cleanRing,
  circleRing,
  closestPointOnSegment,
  pathsArea,
  pathsEdges,
  pointInPaths,
  polylineLength,
  rectRing,
  ringCentroid,
  selfIntersections,
  strokeEnvelope,
  translateRing,
} from '../core/polygon';
import { Vec2, deg, dist, snapAngle, v2 } from '../core/vec';

type Draft =
  | { t: 'rect'; anchor: Vec2; cur: Vec2; clickMode: boolean }
  | { t: 'poly'; pts: Vec2[]; cur: Vec2; closed: boolean }
  | { t: 'circle'; center: Vec2; cur: Vec2; clickMode: boolean }
  | { t: 'measure'; pts: Vec2[]; cur: Vec2 }
  | {
      t: 'move';
      start: Vec2;
      cur: Vec2;
      ids: Id[];
      origin: Record<Id, SceneObject>;
      moved: boolean;
    }
  | {
      t: 'vertex';
      id: Id;
      pathIndex: number;
      index: number;
      isPolyline: boolean;
      origin: SceneObject;
      moved: boolean;
    }
  | { t: 'access-dir'; id: Id; center: Vec2 }
  | { t: 'box'; start: Vec2; cur: Vec2; additive: boolean }
  | { t: 'bg-move'; start: Vec2; baseOrigin: Vec2; moved: boolean }
  | { t: 'bg-rotate'; startAngle: number; baseRotation: number; pivot: Vec2; moved: boolean }
  | { t: 'bg-scale'; startDist: number; baseMpp: number; pivot: Vec2; moved: boolean }
  | null;

const HIT_TOL_PX = 7;

export class EditorController {
  draft: Draft = null;
  snap: SnapResult | null = null;
  guides: Guide[] = [];
  hoverId: Id | undefined;
  cursorWorld: Vec2 = v2();
  cursorScreen: Vec2 = v2();
  private spaceDown = false;
  private shiftDown = false;
  private panning = false;
  private panLast: Vec2 = v2();
  /** 自行判定双击，不依赖 DOM dblclick（触控板 / 自动化工具更可靠） */
  private lastDown: { t: number; p: Vec2 } | null = null;

  constructor(private canvas: HTMLCanvasElement) {}

  /* ------------------------------ 渲染输入 ------------------------------ */

  buildRenderInput(ctx: CanvasRenderingContext2D, w: number, h: number): RenderInput {
    const { model, view } = useEditor.getState();
    camera.setViewport(w, h);
    return {
      ctx,
      width: w,
      height: h,
      camera,
      model,
      view,
      previews: this.buildPreviews(),
      snap: this.snap,
      guides: this.guides,
      boxSelect: this.draft?.t === 'box' ? boundsOfPoints([this.draft.start, this.draft.cur]) : null,
      measure: this.buildMeasure(),
      hoverId: this.hoverId,
      bgImage: model.background ? getImage(model.background.src) : null,
      calib: useEditor.getState().calib,
    };
  }

  render(ctx: CanvasRenderingContext2D, w: number, h: number) {
    render(this.buildRenderInput(ctx, w, h));
  }

  /* ------------------------------- 事件 -------------------------------- */

  onPointerDown(e: PointerEvent) {
    this.canvas.setPointerCapture?.(e.pointerId);
    const st = useEditor.getState();
    const world = this.worldOf(e);
    this.cursorWorld = world;
    this.cursorScreen = this.screenOf(e);

    // 中键 / 空格 / 平移工具 → 拖拽画布
    if (e.button === 1 || this.spaceDown || st.view.activeTool === 'pan') {
      this.panning = true;
      this.panLast = this.screenOf(e);
      return;
    }
    if (e.button !== 0) return;

    const screen = this.screenOf(e);
    const now = performance.now();
    const isDouble =
      !!this.lastDown && now - this.lastDown.t < 340 && dist(this.lastDown.p, screen) < 7;
    this.lastDown = { t: now, p: screen };

    // 两点标定：优先吃掉点击
    if (st.calib) {
      st.setCalibPoint(world);
      return;
    }

    // 背景图编辑：拖动移动 / Alt 旋转 / Shift 缩放
    const bg = st.model.background;
    if (st.view.bgEditing && bg && !bg.locked) {
      const pivot = bg.origin;
      if (e.altKey) {
        st.pushHistory('旋转背景图');
        this.draft = {
          t: 'bg-rotate',
          startAngle: Math.atan2(world.y - pivot.y, world.x - pivot.x),
          baseRotation: bg.rotation,
          pivot,
          moved: false,
        };
        return;
      }
      if (e.shiftKey) {
        st.pushHistory('缩放背景图');
        this.draft = {
          t: 'bg-scale',
          startDist: Math.max(1e-6, dist(pivot, world)),
          baseMpp: bg.metersPerPixel,
          pivot,
          moved: false,
        };
        return;
      }
      st.pushHistory('移动背景图');
      this.draft = { t: 'bg-move', start: world, baseOrigin: { ...bg.origin }, moved: false };
      return;
    }

    const pt = this.applySnap(world, st.view);
    this.shiftDown = e.shiftKey;

    if (isDouble) {
      if (st.view.activeTool === 'select') {
        const hit = this.hitTest(st.model, st.view, world);
        if (hit) {
          useEditor.getState().select([hit.id]);
          this.focusSelection();
          return;
        }
      }
    }

    switch (st.view.activeTool) {
      case 'select':
        return this.handleSelectDown(world, st.view, e.shiftKey);
      case 'rect': {
        if (this.draft?.t === 'rect') {
          this.commitArea(this.draft.anchor, rectRing(this.draft.anchor, pt), st.view);
          this.draft = null;
          return;
        }
        this.draft = { t: 'rect', anchor: pt, cur: pt, clickMode: false };
        return;
      }
      case 'circle': {
        if (this.draft?.t === 'circle') {
          const r = dist(this.draft.center, pt);
          if (r >= 0.15) {
            this.commitArea(this.draft.center, circleRing(this.draft.center, r, 64), st.view);
            this.draft = null;
            return;
          }
        }
        this.draft = { t: 'circle', center: pt, cur: pt, clickMode: false };
        return;
      }
      case 'polygon':
        return this.handlePolygonDown(pt);
      case 'polyline':
        return this.handlePolylineDown(pt, st.view);
      case 'measure': {
        const d = this.draft && this.draft.t === 'measure' ? this.draft : { t: 'measure' as const, pts: [], cur: pt };
        d.pts.push(pt);
        this.draft = d;
        return;
      }
      default:
        return;
    }
  }

  onPointerMove(e: PointerEvent) {
    const st = useEditor.getState();
    const raw = this.worldOf(e);
    this.cursorWorld = raw;
    this.cursorScreen = this.screenOf(e);
    this.shiftDown = e.shiftKey;

    if (this.panning) {
      const s = this.screenOf(e);
      camera.panByScreen(s.x - this.panLast.x, s.y - this.panLast.y);
      this.panLast = s;
      this.syncZoom();
      return;
    }

    // 吸附标记始终跟随光标（绘制过程中也要显示，否则会停在锚点）
    const trackingDraft =
      !this.draft ||
      this.draft.t === 'rect' ||
      this.draft.t === 'circle' ||
      this.draft.t === 'poly' ||
      this.draft.t === 'measure';
    this.snap = st.view.snapEnabled && trackingDraft ? findSnap(this.snapOptions(raw, st.view)) : null;

    // 标定模式下不吸附、不悬停高亮
    if (st.calib) {
      useEditor.getState().setHint({ x: raw.x, y: raw.y, snapLabel: '' });
      return;
    }

    // 拖拽类交互优先
    if (this.draft) {
      switch (this.draft.t) {
        case 'bg-move': {
          const bg = st.model.background;
          if (!bg) return;
          let dx = raw.x - this.draft.start.x;
          let dy = raw.y - this.draft.start.y;
          if (st.view.gridSnap && st.view.snapEnabled) {
            const g = st.view.gridSize;
            dx = Math.round(dx / g) * g;
            dy = Math.round(dy / g) * g;
          }
          st.updateBackground(
            { origin: { x: this.draft.baseOrigin.x + dx, y: this.draft.baseOrigin.y + dy } },
            undefined,
            true,
          );
          this.draft.moved = true;
          return;
        }
        case 'bg-rotate': {
          const bg = st.model.background;
          if (!bg) return;
          const ang = Math.atan2(raw.y - this.draft.pivot.y, raw.x - this.draft.pivot.x);
          let rot = this.draft.baseRotation + (ang - this.draft.startAngle);
          if (this.shiftDown || e.shiftKey) rot = Math.round(rot / (Math.PI / 12)) * (Math.PI / 12);
          st.updateBackground({ rotation: rot }, undefined, true);
          this.draft.moved = true;
          return;
        }
        case 'bg-scale': {
          const bg = st.model.background;
          if (!bg) return;
          const k = dist(this.draft.pivot, raw) / this.draft.startDist;
          const mpp = Math.max(1e-4, this.draft.baseMpp * k);
          st.updateBackground({ metersPerPixel: mpp }, undefined, true);
          this.draft.moved = true;
          return;
        }
        case 'move':
          return this.updateMove(raw);
        case 'vertex':
          return this.updateVertex(raw);
        case 'access-dir':
          return this.updateAccessDir(raw);
        case 'box':
          this.draft.cur = raw;
          return;
        case 'rect':
        case 'circle': {
          const p = this.applySnap(raw, st.view);
          this.draft.cur = this.shiftDown
            ? this.constrainSquare(this.draft.t === 'rect' ? this.draft.anchor : this.draft.center, p)
            : p;
          this.updateHintFromDraft(st.view);
          return;
        }
        case 'poly': {
          const p = this.applySnap(raw, st.view);
          const last = this.draft.pts[this.draft.pts.length - 1];
          this.draft.cur = this.shiftDown && last ? snapAngle(p, last, 45) : p;
          this.updateHintFromDraft(st.view);
          return;
        }
        case 'measure': {
          const p = this.applySnap(raw, st.view);
          this.draft.cur = this.shiftDown && this.draft.pts.length ? snapAngle(p, this.draft.pts[this.draft.pts.length - 1], 45) : p;
          this.updateHintFromDraft(st.view);
          return;
        }
      }
    }

    // 背景编辑模式下不做对象悬停
    if (st.view.bgEditing && st.model.background && !st.model.background.locked) {
      useEditor.getState().setHint({ x: raw.x, y: raw.y, snapLabel: '' });
      return;
    }

    // 悬停
    const pt = this.snap?.point ?? raw;
    const hit = this.hitTest(st.model, st.view, raw);
    const hid = hit?.id;
    if (hid !== this.hoverId) {
      this.hoverId = hid;
      useEditor.getState().hover(hid);
    }
    useEditor.getState().setHint({
      x: pt.x,
      y: pt.y,
      snapLabel: this.snap?.label ?? '',
      length: undefined,
      angle: undefined,
      area: undefined,
    });
  }

  onPointerUp(e: PointerEvent) {
    this.canvas.releasePointerCapture?.(e.pointerId);
    const st = useEditor.getState();
    if (this.panning) {
      this.panning = false;
      return;
    }
    if (!this.draft) return;

    switch (this.draft.t) {
      case 'rect': {
        const r = rectRing(this.draft.anchor, this.draft.cur);
        const b = boundsOfPoints(this.draft.anchor ? [this.draft.anchor, this.draft.cur] : []);
        if (b.maxX - b.minX < 0.15 || b.maxY - b.minY < 0.15) {
          // 视为点击 → 进入两次点击模式
          this.draft.clickMode = true;
          return;
        }
        this.commitArea(this.draft.anchor, r, st.view);
        this.draft = null;
        return;
      }
      case 'circle': {
        const r = dist(this.draft.center, this.draft.cur);
        if (r < 0.15) {
          this.draft.clickMode = true;
          return;
        }
        this.commitArea(this.draft.center, circleRing(this.draft.center, r, 64), st.view);
        this.draft = null;
        return;
      }
      case 'box': {
        const b = boundsOfPoints([this.draft.start, this.draft.cur]);
        if (b.maxX - b.minX > 0.2 || b.maxY - b.minY > 0.2) this.selectInBox(b, this.draft.additive);
        else if (!this.draft.additive) useEditor.getState().select([]);
        this.draft = null;
        return;
      }
      case 'move':
      case 'vertex':
      case 'access-dir':
      case 'bg-move':
      case 'bg-rotate':
      case 'bg-scale':
        this.draft = null;
        this.guides = [];
        return;
      default:
        return;
    }
  }

  onPointerLeave() {
    this.hoverId = undefined;
    useEditor.getState().hover(undefined);
    this.snap = null;
  }

  onDoubleClick(e: MouseEvent) {
    const st = useEditor.getState();
    const world = this.worldOf(e as unknown as PointerEvent);
    const hit = this.hitTest(st.model, st.view, world);
    if (hit) {
      useEditor.getState().select([hit.id]);
      this.focusSelection();
    }
  }

  /**
   * 右键 = 结束当前绘制（取代原先的双击闭合）。
   *
   * 只做「提交」，**不切换工具**：顶点够了就闭合，不够就提示并保留草稿，
   * 用户可以接着点。要退出绘制只有 Esc（或点工具栏别的工具）。
   * 返回 true 表示事件已被消费（调用方负责 preventDefault）。
   */
  onContextMenu(e: MouseEvent): boolean {
    e.preventDefault();
    const st = useEditor.getState();
    if (st.calib) {
      st.cancelCalibration();
      return true;
    }
    const d = this.draft;
    if (d?.t === 'poly') {
      const need = st.view.drawKind === 'road' ? 2 : 3;
      if (d.pts.length >= need) this.closePolygon(st.view);
      else st.showToast(`至少需要 ${need} 个顶点，继续单击加点`);
      return true;
    }
    if (d) {
      // 矩形 / 圆 / 测量：右键放弃这一笔，工具保持不变
      this.draft = null;
      this.guides = [];
      st.setHint({ message: undefined, warning: false });
    }
    return true;
  }

  onWheel(e: WheelEvent) {
    e.preventDefault();
    const s = this.screenOf(e);
    const factor = Math.pow(1.0016, -e.deltaY);
    camera.zoomAt(s, factor);
    this.syncZoom();
  }

  onKeyDown(e: KeyboardEvent): boolean {
    const st = useEditor.getState();
    if (e.key === ' ') this.spaceDown = true;

    const k = e.key.toLowerCase();
    const mod = e.ctrlKey || e.metaKey;

    if (mod && k === 'z' && !e.shiftKey) {
      st.undo();
      return true;
    }
    if ((mod && k === 'y') || (mod && e.shiftKey && k === 'z')) {
      st.redo();
      return true;
    }
    if (mod && k === 'd') {
      st.duplicateSelected();
      return true;
    }
    if (mod && k === 's') {
      return true; // 交给 UI 层保存
    }

    if (e.key === 'Escape') {
      if (st.calib) {
        st.cancelCalibration();
        return true;
      }
      if (this.draft) {
        this.draft = null;
        this.guides = [];
        st.setHint({ message: undefined, warning: false });
      }
      // 退出绘制后一律回到选择工具：用户按 Esc 的意图就是「我不画了」
      if (st.view.activeTool !== 'select') st.setTool('select');
      else st.select([]);
      return true;
    }
    if (e.key === 'Enter') {
      if (this.draft?.t === 'poly') this.closePolygon(st.view);
      return true;
    }
    if (e.key === 'Delete' || e.key === 'Backspace') {
      st.deleteSelected();
      return true;
    }
    if (k === 'f') {
      this.focusSelection();
      return true;
    }
    if (k === 'g' && !mod) {
      // G 生成 / Shift+G 换一版：和顶栏按钮走同一套「选中地块」逻辑
      st.generateParcel(undefined, e.shiftKey);
      return true;
    }
    if (e.key === 'Home') {
      this.fitAll();
      return true;
    }
    if (e.key === 'Shift') {
      this.shiftDown = true;
      return false;
    }
    if (!mod) {
      const tool = TOOL_SHORTCUT[k];
      if (tool) {
        st.setTool(tool);
        return true;
      }
    }
    return false;
  }

  onKeyUp(e: KeyboardEvent) {
    if (e.key === ' ') this.spaceDown = false;
    if (e.key === 'Shift') this.shiftDown = false;
  }

  /* ---------------------------- 选择 / 移动 ---------------------------- */

  private handleSelectDown(raw: Vec2, view: EditorViewState, shift: boolean) {
    const st = useEditor.getState();

    // 1) 顶点拖拽（单选 + 未锁定）
    if (view.selectionIds.length === 1) {
      const obj = st.model.objects[view.selectionIds[0]];
      if (obj && !obj.locked) {
        const vh = this.hitVertex(obj, raw);
        if (vh) {
          st.pushHistory('移动顶点');
          this.draft = {
            t: 'vertex',
            id: obj.id,
            pathIndex: vh.pathIndex,
            index: vh.index,
            isPolyline: obj.kind === 'road',
            origin: structuredClone(obj),
            moved: false,
          };
          return;
        }
        // 2) 出入口方向手柄
        if (obj.kind === 'access') {
          const tip = this.accessTip(obj.pos, obj.dir);
          if (dist(tip, raw) < camera.pxToWorld(14)) {
            st.pushHistory('调整出入口方向');
            this.draft = { t: 'access-dir', id: obj.id, center: obj.pos };
            return;
          }
        }
      }
    }

    const hit = this.hitTest(st.model, view, raw);
    if (!hit) {
      this.draft = { t: 'box', start: raw, cur: raw, additive: shift };
      if (!shift) st.select([]);
      return;
    }

    let ids = view.selectionIds;
    if (shift) {
      st.select([hit.id], true);
      ids = useEditor.getState().view.selectionIds;
    } else if (!view.selectionIds.includes(hit.id)) {
      st.select([hit.id]);
      ids = [hit.id];
    }

    const locked = hit.locked || view.layerLocked[LAYER_OF[hit.kind]];
    if (locked) {
      st.showToast('对象已锁定，先解锁再编辑');
      return;
    }

    // 开始移动
    const origin: Record<Id, SceneObject> = {};
    for (const id of ids) {
      const o = useEditor.getState().model.objects[id];
      if (o && !o.locked) origin[id] = structuredClone(o);
    }
    this.draft = { t: 'move', start: raw, cur: raw, ids: Object.keys(origin), origin, moved: false };
  }

  private updateMove(raw: Vec2) {
    if (!this.draft || this.draft.t !== 'move') return;
    const st = useEditor.getState();
    const d = this.draft;
    let dx = raw.x - d.start.x;
    let dy = raw.y - d.start.y;

    // 网格吸附（整栋建筑移动时优先网格）
    if (st.view.gridSnap && st.view.snapEnabled) {
      const g = st.view.gridSize;
      dx = Math.round(dx / g) * g;
      dy = Math.round(dy / g) * g;
    }

    // 对齐参考线
    const movingIds = new Set(d.ids);
    const others = this.otherBounds(st.model, st.view, movingIds);
    const moving = this.boundsOfObjects(d.origin);
    if (moving) {
      const shifted: Bounds = {
        minX: moving.minX + dx,
        minY: moving.minY + dy,
        maxX: moving.maxX + dx,
        maxY: moving.maxY + dy,
      };
      const tol = camera.pxToWorld(6);
      const gd = guideSnapDelta(shifted, others, tol);
      dx += gd.dx;
      dy += gd.dy;
      this.guides = alignmentGuides({ ...shifted, minX: shifted.minX + gd.dx, minY: shifted.minY + gd.dy, maxX: shifted.maxX + gd.dx, maxY: shifted.maxY + gd.dy }, others, tol);
    } else {
      this.guides = [];
    }

    if (!d.moved && Math.hypot(dx, dy) * camera.scale > 2) {
      st.pushHistory('移动对象');
      d.moved = true;
    }
    d.cur = { x: d.start.x + dx, y: d.start.y + dy };

    const delta = { x: dx, y: dy };
    st.setModel(translateObjects(st.model, d.origin, delta));
    st.setHint({ x: raw.x, y: raw.y, snapLabel: '', length: Math.hypot(dx, dy) });
  }

  private updateVertex(raw: Vec2) {
    if (!this.draft || this.draft.t !== 'vertex') return;
    const st = useEditor.getState();
    const d = this.draft;
    const snapped = st.view.snapEnabled ? findSnap(this.snapOptions(raw, st.view)) : null;
    const p = snapped?.point ?? raw;
    if (!d.moved) {
      st.pushHistory('移动顶点');
      d.moved = true;
    }
    const obj = structuredClone(d.origin);
    if (d.isPolyline && obj.kind === 'road') {
      obj.centerline[d.index] = p;
    } else if (isAreaObj(obj)) {
      const paths = areaPaths(obj);
      if (paths?.[d.pathIndex]) paths[d.pathIndex][d.index] = p;
    }
    st.setModel({ ...st.model, objects: { ...st.model.objects, [d.id]: obj as SceneObject } });
  }

  private updateAccessDir(raw: Vec2) {
    if (!this.draft || this.draft.t !== 'access-dir') return;
    const st = useEditor.getState();
    const obj = st.model.objects[this.draft.id];
    if (!obj || obj.kind !== 'access') return;
    const ang = Math.atan2(raw.y - this.draft.center.y, raw.x - this.draft.center.x);
    const snapped = this.shiftDown ? Math.round(ang / (Math.PI / 4)) * (Math.PI / 4) : ang;
    st.updateObject(obj.id, { dir: snapped } as Partial<SceneObject>);
  }

  /* ------------------------------ 绘制提交 ----------------------------- */

  private handlePolygonDown(pt: Vec2) {
    const view = useEditor.getState().view;
    if (view.drawKind === 'access') {
      this.placeAccess(pt);
      return;
    }
    if (!this.draft || this.draft.t !== 'poly') {
      this.draft = { t: 'poly', pts: [pt], cur: pt, closed: false };
      return;
    }
    // 点回起点 → 闭合
    if (this.draft.pts.length >= 3 && dist(this.draft.pts[0], pt) < camera.pxToWorld(9)) {
      this.closePolygon(view);
      return;
    }
    const last = this.draft.pts[this.draft.pts.length - 1];
    if (last && dist(last, pt) < 0.02) return;
    this.draft.pts.push(pt);
    this.draft.cur = pt;
  }

  private handlePolylineDown(pt: Vec2, view: EditorViewState) {
    if (view.drawKind === 'access') {
      this.placeAccess(pt);
      return;
    }
    if (!this.draft || this.draft.t !== 'poly') {
      this.draft = { t: 'poly', pts: [pt], cur: pt, closed: false };
      return;
    }
    const last = this.draft.pts[this.draft.pts.length - 1];
    if (last && dist(last, pt) < 0.02) return;
    this.draft.pts.push(pt);
    this.draft.cur = pt;
  }

  private closePolygon(view: EditorViewState) {
    if (!this.draft || this.draft.t !== 'poly') return;
    const pts = this.draft.pts.slice();
    this.draft = null;
    if (view.drawKind === 'road') {
      if (pts.length < 2) return;
      this.commitRoad(pts);
      return;
    }
    if (pts.length < 3) {
      useEditor.getState().showToast('多边形至少需要 3 个顶点');
      return;
    }
    const ring = cleanRing(pts);
    const self = selfIntersections(ring);
    if (self.length) {
      useEditor.getState().showToast('多边形自交，已拒绝创建');
      useEditor.getState().setHint({ message: '多边形自交（Invalid Polygon）', warning: true });
      return;
    }
    this.commitArea(ringCentroid(ring), ring, view);
  }

  private commitArea(_ref: Vec2, ring: Ring, view: EditorViewState) {
    const st = useEditor.getState();
    const clean = cleanRing(ring);
    if (clean.length < 3) return;
    const paths: Paths = [clean];
    const kind: DrawKind = view.drawKind === 'access' || view.drawKind === 'road' ? 'parcel' : view.drawKind;

    let obj: SceneObject;
    switch (kind) {
      case 'site':
        obj = makeSite(paths);
        break;
      case 'building':
        obj = makeBuilding(paths, { height: 12 });
        break;
      case 'nobuild':
        obj = makeNoBuild(paths);
        break;
      case 'ground':
        obj = makeGround(paths, 'paving');
        break;
      default:
        obj = makeParcel(paths, { name: `地块 ${st.model.order.filter((i) => st.model.objects[i]?.kind === 'parcel').length + 1}` });
        break;
    }

    st.pushHistory(`创建${kind}`);
    st.setModel(addTo(st.model, obj));
    st.select([obj.id]);
    st.setHint({ area: pathsArea(paths), message: undefined, warning: false });
  }

  private commitRoad(pts: Vec2[]) {
    const st = useEditor.getState();
    const road = makeRoad(pts, { roadClass: 'local', width: 6, sidewalk: true });
    st.pushHistory('创建道路');
    st.setModel(addTo(st.model, road));
    st.select([road.id]);
    st.setHint({ length: polylineLength(pts), message: undefined, warning: false });
  }

  private placeAccess(pos: Vec2) {
    const st = useEditor.getState();
    const gate = makeAccess(pos, { width: 8, dir: 0 });
    st.pushHistory('创建出入口');
    st.setModel(addTo(st.model, gate));
    st.select([gate.id]);
    st.showToast('已放置出入口，拖动箭头可调整朝向');
  }

  /* ------------------------------- 命中测试 ------------------------------ */

  hitTest(model: ProjectModel, view: EditorViewState, world: Vec2): SceneObject | null {
    const objs = pickableObjects(model, view);
    const tol = camera.pxToWorld(HIT_TOL_PX);
    // 反向：后绘制的在上
    for (let i = objs.length - 1; i >= 0; i--) {
      const o = objs[i];
      if (this.hits(o, world, tol)) return o;
    }
    return null;
  }

  private hits(o: SceneObject, p: Vec2, tol: number): boolean {
    const paths = areaPaths(o);
    if (paths) {
      if (pointInPaths(p, paths)) return true;
      for (const e of pathsEdges(paths)) {
        if (closestPointOnSegment(p, e.a, e.b).dist <= tol) return true;
      }
      return false;
    }
    if (o.kind === 'road') {
      const halfW = o.width / 2;
      for (let i = 0; i < o.centerline.length - 1; i++) {
        if (closestPointOnSegment(p, o.centerline[i], o.centerline[i + 1]).dist <= halfW + tol) return true;
      }
      return false;
    }
    if (o.kind === 'access') {
      return dist(p, o.pos) <= Math.max(tol, o.width / 2);
    }
    return false;
  }

  private hitVertex(o: SceneObject, p: Vec2): { pathIndex: number; index: number } | null {
    const tol = camera.pxToWorld(8);
    if (o.kind === 'road') {
      for (let i = 0; i < o.centerline.length; i++) {
        if (dist(o.centerline[i], p) <= tol) return { pathIndex: 0, index: i };
      }
      return null;
    }
    const paths = areaPaths(o);
    if (!paths) return null;
    for (let pi = 0; pi < paths.length; pi++) {
      for (let vi = 0; vi < paths[pi].length; vi++) {
        if (dist(paths[pi][vi], p) <= tol) return { pathIndex: pi, index: vi };
      }
    }
    return null;
  }

  private selectInBox(b: Bounds, additive: boolean) {
    const st = useEditor.getState();
    const objs = pickableObjects(st.model, st.view);
    const ids: Id[] = [];
    for (const o of objs) {
      const ob = objectBounds(o);
      if (!ob) continue;
      const overlap = !(ob.maxX < b.minX || ob.minX > b.maxX || ob.maxY < b.minY || ob.minY > b.maxY);
      if (overlap) ids.push(o.id);
    }
    st.select(ids, additive);
  }

  /* -------------------------------- 相机 ------------------------------- */

  focusSelection() {
    const st = useEditor.getState();
    const b = selectionBounds(st.model, st.view.selectionIds);
    if (b) {
      camera.fitBounds(b, 120);
      this.syncZoom();
    } else {
      this.fitAll();
    }
  }

  fitAll() {
    const st = useEditor.getState();
    const objs = st.model.order.map((id) => st.model.objects[id]).filter((o): o is SceneObject => !!o);
    let b: Bounds | null = null;
    for (const o of objs) {
      const ob = objectBounds(o);
      if (ob) b = b ? unionB(b, ob) : ob;
    }
    if (st.model.background?.visible) {
      const bb = backgroundBounds(st.model.background);
      if (Number.isFinite(bb.minX)) b = b ? unionB(b, bb) : bb;
    }
    if (b) camera.fitBounds(b, 64);
    this.syncZoom();
  }

  syncZoom() {
    useEditor.getState().patchSettings({ zoom: camera.scale });
  }

  /* ------------------------------ 辅助方法 ----------------------------- */

  private worldOf(e: { clientX: number; clientY: number }): Vec2 {
    return camera.screenToWorld(this.screenOf(e));
  }

  private screenOf(e: { clientX: number; clientY: number }): Vec2 {
    const r = this.canvas.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  private snapOptions(world: Vec2, view: EditorViewState) {
    const st = useEditor.getState();
    return {
      objects: pickableObjects(st.model, view),
      world,
      radiusWorld: camera.pxToWorld(SNAP_RADIUS_PX),
      gridSize: view.gridSize,
      gridSnap: view.gridSnap,
      gridVisible: view.gridVisible,
      enabled: view.snapEnabled,
    };
  }

  private applySnap(world: Vec2, view: EditorViewState): Vec2 {
    if (!view.snapEnabled) return world;
    const s = findSnap(this.snapOptions(world, view));
    return s?.point ?? world;
  }

  private constrainSquare(anchor: Vec2, p: Vec2): Vec2 {
    const dx = p.x - anchor.x;
    const dy = p.y - anchor.y;
    const m = Math.max(Math.abs(dx), Math.abs(dy));
    return { x: anchor.x + Math.sign(dx || 1) * m, y: anchor.y + Math.sign(dy || 1) * m };
  }

  private accessTip(pos: Vec2, dir: number): Vec2 {
    const r = camera.pxToWorld(24);
    return { x: pos.x + Math.cos(dir) * r, y: pos.y + Math.sin(dir) * r };
  }

  private otherBounds(model: ProjectModel, view: EditorViewState, exclude: Set<Id>) {
    return pickableObjects(model, view)
      .filter((o) => !exclude.has(o.id))
      .map((o) => ({ id: o.id, bounds: objectBounds(o) }))
      .filter((x): x is { id: Id; bounds: Bounds } => !!x.bounds);
  }

  private boundsOfObjects(objs: Record<Id, SceneObject>): Bounds | null {
    let b: Bounds | null = null;
    for (const o of Object.values(objs)) {
      const ob = objectBounds(o);
      if (ob) b = b ? unionB(b, ob) : ob;
    }
    return b;
  }

  /* ------------------------------- 预览构建 ----------------------------- */

  private buildPreviews(): Preview[] {
    const out: Preview[] = [];
    const d = this.draft;
    const st = useEditor.getState();
    // 出入口放置预演：无草稿时也在光标处画一个门
    if (!d) {
      if (
        st.view.drawKind === 'access' &&
        (st.view.activeTool === 'polygon' || st.view.activeTool === 'polyline')
      ) {
        out.push({ kind: 'handle', p: this.cursorWorld, dir: 0, width: 8 });
      }
      return out;
    }

    switch (d.t) {
      case 'rect': {
        const ring = rectRing(d.anchor, d.cur);
        const w = Math.abs(d.cur.x - d.anchor.x);
        const h = Math.abs(d.cur.y - d.anchor.y);
        out.push({
          kind: 'ring',
          ring,
          label: `${w.toFixed(2)} × ${h.toFixed(2)} m   ${fmtArea(w * h)}`,
          ghost: d.clickMode,
        });
        break;
      }
      case 'circle': {
        const r = dist(d.center, d.cur);
        out.push({
          kind: 'ring',
          ring: circleRing(d.center, r, 64),
          label: `R ${r.toFixed(2)} m   ${fmtArea(Math.PI * r * r)}`,
          ghost: d.clickMode,
        });
        break;
      }
      case 'poly': {
        const pts = [...d.pts, d.cur];
        if (st.view.drawKind === 'road') {
          out.push({ kind: 'polyline', pts, width: 6, label: polylineLength(pts).toFixed(2) + ' m' });
        } else if (pts.length >= 3) {
          const ring = [...d.pts, d.cur];
          const self = selfIntersections(ring);
          out.push({
            kind: 'ring',
            ring,
            label: `${fmtArea(Math.abs(ringAreaSafe(ring)))}   边长 ${polylineLength([...pts, pts[0]]).toFixed(1)} m`,
            invalid: self.length > 0,
          });
        } else {
          out.push({ kind: 'polyline', pts, label: polylineLength(pts).toFixed(2) + ' m' });
        }
        // 顶点标记
        for (const p of d.pts) out.push({ kind: 'text', p, text: '' });
        break;
      }
      case 'measure': {
        break;
      }
      case 'move': {
        break;
      }
      default:
        break;
    }
    return out;
  }

  private buildMeasure(): MeasureOverlay | null {
    const st = useEditor.getState();
    if (this.draft?.t === 'measure') {
      return { pts: this.draft.pts, cursor: this.draft.pts.length ? this.draft.cur : undefined, mode: st.view.measureMode };
    }
    return null;
  }

  private updateHintFromDraft(view: EditorViewState) {
    const d = this.draft;
    if (!d) return;
    if (d.t === 'rect') {
      const w = Math.abs(d.cur.x - d.anchor.x);
      const h = Math.abs(d.cur.y - d.anchor.y);
      useEditor.getState().setHint({ x: d.cur.x, y: d.cur.y, length: Math.hypot(w, h), area: w * h, snapLabel: this.snap?.label ?? '' });
    } else if (d.t === 'poly') {
      const pts = [...d.pts];
      const last = pts[pts.length - 1];
      const len = last ? dist(last, d.cur) : 0;
      const ang = last ? deg(Math.atan2(d.cur.y - last.y, d.cur.x - last.x)) : 0;
      const self = pts.length >= 2 && selfIntersections([...pts, d.cur]).length > 0;
      const need = view.drawKind === 'road' ? 2 : 3;
      useEditor.getState().setHint({
        x: d.cur.x,
        y: d.cur.y,
        snapLabel: this.snap?.label ?? '',
        length: len,
        angle: ang,
        area: view.drawKind === 'road' ? undefined : pts.length >= 2 ? Math.abs(ringAreaSafe([...pts, d.cur])) : undefined,
        message: self
          ? '自交：Invalid Polygon'
          : pts.length >= need
            ? '右键 / 回车 结束绘制'
            : '单击继续加点 · Esc 退出',
        warning: self,
      });
    } else if (d.t === 'measure') {
      const pts = [...d.pts, d.cur];
      useEditor.getState().setHint({
        x: d.cur.x,
        y: d.cur.y,
        length: polylineLength(pts),
        area: view.measureMode === 'area' && pts.length >= 3 ? Math.abs(ringAreaSafe(pts)) : undefined,
        snapLabel: this.snap?.label ?? '',
      });
    }
  }
}

/* --------------------------------- 工具函数 -------------------------------- */

function ringAreaSafe(ring: Ring): number {
  let s = 0;
  for (let i = 0; i < ring.length; i++) {
    const a = ring[i];
    const b = ring[(i + 1) % ring.length];
    s += a.x * b.y - b.x * a.y;
  }
  return s / 2;
}

function unionB(a: Bounds, b: Bounds): Bounds {
  return {
    minX: Math.min(a.minX, b.minX),
    minY: Math.min(a.minY, b.minY),
    maxX: Math.max(a.maxX, b.maxX),
    maxY: Math.max(a.maxY, b.maxY),
  };
}

function translateObjects(model: ProjectModel, origin: Record<Id, SceneObject>, delta: Vec2): ProjectModel {
  const objects = { ...model.objects };
  for (const [id, src] of Object.entries(origin)) {
    const clone = structuredClone(src);
    if (isAreaObj(clone)) {
      const p = areaPaths(clone);
      if (p) {
        const moved: Paths = p.map((r) => translateRing(r, delta));
        if (clone.kind === 'building') clone.footprint = moved;
        else (clone as { boundary: Paths }).boundary = moved;
      }
    } else if (clone.kind === 'road') {
      clone.centerline = clone.centerline.map((p) => ({ x: p.x + delta.x, y: p.y + delta.y }));
    } else if (clone.kind === 'access') {
      clone.pos = { x: clone.pos.x + delta.x, y: clone.pos.y + delta.y };
    }
    objects[id] = clone as SceneObject;
  }
  return { ...model, objects };
}

/** 绘制包络（供外部复用） */
export { strokeEnvelope, fmtLen };
