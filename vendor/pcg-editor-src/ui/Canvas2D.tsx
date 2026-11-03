import { useEffect, useRef } from 'react';
import { camera } from '../editor/camera';
import { EditorController } from '../editor/controller';
import { useEditor } from '../editor/store';
import { DRAW_KIND_LABEL, TOOL_LABEL } from '../editor/viewstate';

export function Canvas2D() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const ctrlRef = useRef<EditorController | null>(null);
  const toast = useEditor((s) => s.toast);
  const activeTool = useEditor((s) => s.view.activeTool);
  const drawKind = useEditor((s) => s.view.drawKind);
  const calib = useEditor((s) => s.calib);

  useEffect(() => {
    const canvas = canvasRef.current;
    const host = canvas?.parentElement;
    if (!canvas || !host) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const ctrl = new EditorController(canvas);
    ctrlRef.current = ctrl;
    if (import.meta.env.DEV) (window as unknown as Record<string, unknown>).__ctrl = ctrl;

    let cssW = 0;
    let cssH = 0;
    const resize = () => {
      const r = host.getBoundingClientRect();
      cssW = Math.max(1, r.width);
      cssH = Math.max(1, r.height);
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      canvas.width = Math.round(cssW * dpr);
      canvas.height = Math.round(cssH * dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      camera.setViewport(cssW, cssH);
    };
    resize();
    ctrl.fitAll();
    ctrl.syncZoom();

    const ro = new ResizeObserver(resize);
    ro.observe(host);

    let raf = 0;
    const loop = () => {
      ctrl.render(ctx, cssW, cssH);
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);

    const pd = (e: PointerEvent) => {
      canvas.focus();
      ctrl.onPointerDown(e);
    };
    const pm = (e: PointerEvent) => ctrl.onPointerMove(e);
    const pu = (e: PointerEvent) => ctrl.onPointerUp(e);
    const pl = () => ctrl.onPointerLeave();
    const dc = (e: MouseEvent) => ctrl.onDoubleClick(e);
    const wh = (e: WheelEvent) => ctrl.onWheel(e);
    const cm = (e: MouseEvent) => ctrl.onContextMenu(e);

    canvas.addEventListener('pointerdown', pd);
    canvas.addEventListener('pointermove', pm);
    canvas.addEventListener('pointerup', pu);
    canvas.addEventListener('pointerleave', pl);
    canvas.addEventListener('dblclick', dc);
    canvas.addEventListener('wheel', wh, { passive: false });
    canvas.addEventListener('contextmenu', cm);

    const kd = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return;
      if (ctrl.onKeyDown(e)) e.preventDefault();
    };
    const ku = (e: KeyboardEvent) => ctrl.onKeyUp(e);
    window.addEventListener('keydown', kd);
    window.addEventListener('keyup', ku);

    return () => {
      cancelAnimationFrame(raf);
      ro.disconnect();
      canvas.removeEventListener('pointerdown', pd);
      canvas.removeEventListener('pointermove', pm);
      canvas.removeEventListener('pointerup', pu);
      canvas.removeEventListener('pointerleave', pl);
      canvas.removeEventListener('dblclick', dc);
      canvas.removeEventListener('wheel', wh);
      canvas.removeEventListener('contextmenu', cm);
      window.removeEventListener('keydown', kd);
      window.removeEventListener('keyup', ku);
      ctrlRef.current = null;
    };
  }, []);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => useEditor.getState().showToast(''), 2600);
    return () => clearTimeout(t);
  }, [toast]);

  const cls = `stage tool-${activeTool}`;
  const isDraw = activeTool !== 'select' && activeTool !== 'pan';

  return (
    <div className={cls}>
      <canvas ref={canvasRef} tabIndex={0} />
      <div className="canvas-badge">
        {calib ? (
          <>
            <b>比例标定</b>
            <span>{calib.a ? (calib.b ? 'A、B 已选，右侧输入实际距离' : '已点 A，请点 B') : '请点击第一个点 A'}</span>
          </>
        ) : (
          <>
        <b>{TOOL_LABEL[activeTool]}</b>
        {isDraw && <span>· 绘制为 {DRAW_KIND_LABEL[drawKind]}</span>}
        {activeTool === 'select' && <span>· 拖动移动 / 双击聚焦 / Shift 多选</span>}
        {isDraw && <span>· 右键 / 回车 结束 · Esc 退出</span>}
          </>
        )}
      </div>
      {toast && <div className="toast">{toast}</div>}
    </div>
  );
}
