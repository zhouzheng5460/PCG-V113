import { useEditor } from '../editor/store';
import { TOOL_LABEL } from '../editor/viewstate';
import { fmtArea } from '../editor/render';

export function StatusBar() {
  const hint = useEditor((s) => s.hint);
  const view = useEditor((s) => s.view);
  const model = useEditor((s) => s.model);
  const selCount = view.selectionIds.length;

  const fmt = (n: number, d = 2) => n.toFixed(d);

  return (
    <div className="statusbar">
      <span>
        X <b>{fmt(hint.x)}</b> Y <b>{fmt(hint.y)}</b>
      </span>
      <span className="sep" />
      <span>
        长度 <b>{hint.length !== undefined ? `${fmt(hint.length)} m` : '—'}</b>
      </span>
      <span>
        角度 <b>{hint.angle !== undefined ? `${fmt(hint.angle, 1)}°` : '—'}</b>
      </span>
      <span>
        面积 <b>{hint.area !== undefined ? fmtArea(hint.area) : '—'}</b>
      </span>
      <span className="sep" />
      <span>
        Snap: <b className={hint.snapLabel ? 'ok' : ''}>{hint.snapLabel || (view.snapEnabled ? 'on' : 'off')}</b>
      </span>
      <span>
        Grid: <b>{view.gridSize}m</b>
        {!view.gridSnap && ' (不吸附)'}
      </span>
      <span className="sep" />
      <span>
        工具 <b>{TOOL_LABEL[view.activeTool]}</b>
      </span>
      <span>
        选中 <b>{selCount}</b>
      </span>
      <span className="sep" />
      <span>
        缩放 <b>{fmt(view.zoom, 2)}</b> px/m
      </span>
      <span className="sep" />
      <span>对象 {model.order.length}</span>
      {hint.message && (
        <>
          <span className="sep" />
          <span className={hint.warning ? 'warn' : ''}>⚠ {hint.message}</span>
        </>
      )}
    </div>
  );
}
