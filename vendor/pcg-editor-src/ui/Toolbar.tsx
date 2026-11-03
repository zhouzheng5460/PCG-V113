import { useEditor } from '../editor/store';
import { DrawKind, ToolId } from '../editor/viewstate';

const Icon = ({ d, fill = false }: { d: string; fill?: boolean }) => (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round">
    <path d={d} fill={fill ? 'currentColor' : 'none'} fillOpacity={fill ? 0.18 : 0} />
  </svg>
);

const ICONS = {
  select: 'M3 2.5 L10.5 8 L7 8.8 L8.8 12.6 L7.4 13.3 L5.6 9.4 L3 11.6 Z',
  site: 'M2.5 3.5 H13.5 V12.5 H2.5 Z M2.5 6.5 H13.5 M6 3.5 V12.5',
  parcel: 'M3 3 H13 V13 H3 Z',
  building: 'M3.5 13 V4.5 H9 V13 M9 7.5 H12.5 V13 M2 13 H14',
  road: 'M6 2 V14 M10 2 V14 M8 3.5 V5 M8 7 V8.5 M8 10.5 V12',
  gate: 'M2.5 12.5 H6 M10 12.5 H13.5 M6 12.5 V8 H10 V12.5',
  nobuild: 'M3 3 H13 V13 H3 Z M5 5 L11 11 M11 5 L5 11',
  rect: 'M3 4 H13 V12 H3 Z',
  polygon: 'M8 2.5 L13.5 6.5 L11.5 13 H4.5 L2.5 6.5 Z',
  polyline: 'M2.5 12 L6 5 L9 9.5 L13.5 3.5',
  circle: 'M8 2.5 A5.5 5.5 0 1 0 8.01 2.5',
  measure: 'M2.5 6.5 L6.5 2.5 L13.5 9.5 L9.5 13.5 Z M5 8.5 L6.5 7 M7 10.5 L8.5 9 M9 6.5 L10.5 5',
};

interface SemanticDef {
  kind: DrawKind;
  tool: ToolId;
  label: string;
  icon: keyof typeof ICONS;
}

const SEMANTIC: SemanticDef[] = [
  { kind: 'site', tool: 'polygon', label: '园区', icon: 'site' },
  { kind: 'parcel', tool: 'polygon', label: '地块', icon: 'parcel' },
  { kind: 'building', tool: 'rect', label: '建筑', icon: 'building' },
  { kind: 'road', tool: 'polyline', label: '道路', icon: 'road' },
  { kind: 'access', tool: 'polygon', label: '出入口', icon: 'gate' },
  { kind: 'nobuild', tool: 'polygon', label: '禁建', icon: 'nobuild' },
];

const GEOM: { tool: ToolId; label: string; icon: keyof typeof ICONS; key: string }[] = [
  { tool: 'rect', label: '矩形', icon: 'rect', key: 'R' },
  { tool: 'polygon', label: '多边形', icon: 'polygon', key: 'P' },
  { tool: 'polyline', label: '折线', icon: 'polyline', key: 'L' },
  { tool: 'circle', label: '圆', icon: 'circle', key: 'C' },
];

export function Toolbar() {
  const view = useEditor((s) => s.view);
  const setTool = useEditor((s) => s.setTool);
  const setDrawKind = useEditor((s) => s.setDrawKind);
  const applyBoolean = useEditor((s) => s.applyBoolean);
  const applyOffset = useEditor((s) => s.applyOffset);
  const undo = useEditor((s) => s.undo);
  const redo = useEditor((s) => s.redo);
  const canUndo = useEditor((s) => s.past.length > 0);
  const canRedo = useEditor((s) => s.future.length > 0);
  const selCount = view.selectionIds.length;

  return (
    <div className="toolbar">
      <button
        className={`tool ${view.activeTool === 'select' ? 'on' : ''}`}
        onClick={() => setTool('select')}
        title="选择 / 移动 (V)"
      >
        <Icon d={ICONS.select} fill />
        选择
      </button>

      <div className="tool-sep" />
      <div className="tool-label">语义</div>
      {SEMANTIC.map((s) => {
        const on = view.drawKind === s.kind && view.activeTool !== 'select' && view.activeTool !== 'pan';
        return (
          <button
            key={s.kind}
            className={`tool ${on ? 'on' : ''}`}
            onClick={() => {
              setDrawKind(s.kind);
              setTool(s.tool);
            }}
            title={`绘制${s.label}`}
          >
            <Icon d={ICONS[s.icon]} />
            {s.label}
          </button>
        );
      })}

      <div className="tool-sep" />
      <div className="tool-label">几何</div>
      {GEOM.map((g) => (
        <button
          key={g.tool}
          className={`tool ${view.activeTool === g.tool ? 'on' : ''}`}
          onClick={() => setTool(g.tool)}
          title={`${g.label} (${g.key})`}
        >
          <Icon d={ICONS[g.icon]} />
          {g.label}
        </button>
      ))}

      <div className="tool-sep" />
      <button
        className={`tool ${view.activeTool === 'measure' ? 'on' : ''}`}
        onClick={() => setTool('measure')}
        title="测量距离 / 面积 (M)"
      >
        <Icon d={ICONS.measure} />
        测量
      </button>

      <div className="tool-sep" />
      <div className="tool-label">布尔</div>
      <div className="mini-grid c3">
        <button className="mini" onClick={() => applyBoolean('union')} disabled={selCount < 2} title="合并 Union（至少 2 个面）">
          ∪
        </button>
        <button className="mini" onClick={() => applyBoolean('subtract')} disabled={selCount < 2} title="减去 Subtract（第一个选中为主对象）">
          −
        </button>
        <button className="mini" onClick={() => applyBoolean('intersect')} disabled={selCount < 2} title="取交集 Intersect">
          ∩
        </button>
      </div>
      <div className="tool-label">偏移</div>
      <div className="mini-grid c2">
        <button className="mini" onClick={() => applyOffset(-3)} disabled={selCount !== 1} title="向内偏移 3m（退界 / 绿化带）">
          内缩
        </button>
        <button className="mini" onClick={() => applyOffset(3)} disabled={selCount !== 1} title="向外偏移 3m">
          外扩
        </button>
      </div>
      <div className="tool-sep" />
      <div className="mini-grid c2">
        <button className="mini" onClick={undo} disabled={!canUndo} title="撤销 (Ctrl+Z)">
          ↶
        </button>
        <button className="mini" onClick={redo} disabled={!canRedo} title="重做 (Ctrl+Shift+Z)">
          ↷
        </button>
      </div>
    </div>
  );
}
