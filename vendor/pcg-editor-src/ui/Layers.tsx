import { LayerKey } from '../core/model';
import { useEditor } from '../editor/store';

const LAYERS: { key: LayerKey; label: string; hint: string }[] = [
  { key: 'site', label: '园区边界', hint: 'Site' },
  { key: 'parcels', label: '地块', hint: 'Parcels' },
  { key: 'roads', label: '道路', hint: 'Roads' },
  { key: 'buildings', label: '建筑', hint: 'Buildings' },
  { key: 'access', label: '出入口', hint: 'Access' },
  { key: 'ground', label: '铺装', hint: 'Ground' },
  { key: 'landscape', label: '禁建 / 景观', hint: 'Landscape' },
];

const EyeOn = () => (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
    <path d="M1.5 8s2.5-4 6.5-4 6.5 4 6.5 4-2.5 4-6.5 4S1.5 8 1.5 8Z" />
    <circle cx="8" cy="8" r="1.8" />
  </svg>
);
const EyeOff = () => (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
    <path d="M2.5 2.5 13.5 13.5M4.2 4.5C2.7 5.6 1.5 8 1.5 8s2.5 4 6.5 4c1.2 0 2.2-.3 3.1-.8M7 4.1c.3 0 .6-.1 1-.1 4 0 6.5 4 6.5 4s-.7 1.1-1.9 2.2" />
  </svg>
);
const LockClosed = () => (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
    <rect x="3.5" y="7" width="9" height="6.5" rx="1.2" />
    <path d="M5.8 7V5.2a2.2 2.2 0 0 1 4.4 0V7" />
  </svg>
);
const LockOpen = () => (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.4">
    <rect x="3.5" y="7" width="9" height="6.5" rx="1.2" />
    <path d="M5.8 7V5.2a2.2 2.2 0 0 1 4.2-.9" />
  </svg>
);

export function Layers() {
  const model = useEditor((s) => s.model);
  const view = useEditor((s) => s.view);
  const toggleVisible = useEditor((s) => s.toggleLayerVisible);
  const toggleLocked = useEditor((s) => s.toggleLayerLocked);

  const counts: Record<string, number> = {};
  for (const id of model.order) {
    const o = model.objects[id];
    if (!o) continue;
    const layer = {
      site: 'site',
      parcel: 'parcels',
      building: 'buildings',
      road: 'roads',
      access: 'access',
      nobuild: 'landscape',
      ground: 'ground',
    }[o.kind];
    counts[layer] = (counts[layer] ?? 0) + 1;
  }

  return (
    <div className="panel">
      <h3>
        图层 <span className="count">语义层</span>
      </h3>
      {LAYERS.map((l) => {
        const vis = view.layerVisibility[l.key];
        const lock = view.layerLocked[l.key];
        return (
          <div key={l.key} className={`layer ${vis ? '' : 'off'}`}>
            <button
              className={`ico ${vis ? 'active' : ''}`}
              onClick={() => toggleVisible(l.key)}
              title={vis ? '隐藏图层' : '显示图层'}
            >
              {vis ? <EyeOn /> : <EyeOff />}
            </button>
            <button
              className={`ico ${lock ? 'lit' : ''}`}
              onClick={() => toggleLocked(l.key)}
              title={lock ? '解锁图层' : '锁定图层'}
            >
              {lock ? <LockClosed /> : <LockOpen />}
            </button>
            <span className="name">{l.label}</span>
            <span style={{ fontFamily: 'var(--mono)', fontSize: 10.5, color: 'var(--dim-2)' }}>
              {counts[l.key] ?? 0}
            </span>
          </div>
        );
      })}
    </div>
  );
}
