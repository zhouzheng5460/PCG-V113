import { useEffect, useState } from 'react';
import {
  BuildingStyle,
  GroundSurface,
  LANDUSE_LABEL,
  LandUse,
  KIND_LABEL,
  ParcelObj,
  ROAD_LABEL,
  SURFACE_LABEL,
  RoadClass,
  SceneObject,
  areaPaths,
  isAreaObj,
} from '../core/model';
import { boundsOfPaths, ringPerimeter, pathsArea, ringCentroid } from '../core/polygon';
import { useEditor } from '../editor/store';
import { fmtArea } from '../editor/render';

function Row({ k, v }: { k: string; v: string }) {
  return (
    <div className="row">
      <span className="k">{k}</span>
      <span className="v">{v}</span>
    </div>
  );
}

function Num({
  label,
  value,
  step = 1,
  digits = 2,
  commit,
}: {
  label: string;
  value: number;
  step?: number;
  digits?: number;
  commit: (n: number) => void;
}) {
  const [txt, setTxt] = useState(value.toFixed(digits));
  useEffect(() => setTxt(value.toFixed(digits)), [value, digits]);
  return (
    <div className="field">
      <label>{label}</label>
      <input
        type="number"
        step={step}
        value={txt}
        onChange={(e) => setTxt(e.target.value)}
        onBlur={() => {
          const n = Number(txt);
          if (Number.isFinite(n) && Math.abs(n - value) > 1e-6) commit(n);
          else setTxt(value.toFixed(digits));
        }}
        onKeyDown={(e) => {
          if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
        }}
      />
    </div>
  );
}

function Sel<T extends string>({
  label,
  value,
  options,
  commit,
}: {
  label: string;
  value: T;
  options: Record<string, string>;
  commit: (v: T) => void;
}) {
  return (
    <div className="field">
      <label>{label}</label>
      <select value={value} onChange={(e) => commit(e.target.value as T)}>
        {Object.entries(options).map(([k, v]) => (
          <option key={k} value={k}>
            {v}
          </option>
        ))}
      </select>
    </div>
  );
}

function Toggle({ label, on, commit }: { label: string; on: boolean; commit: () => void }) {
  return (
    <div className="field">
      <label>{label}</label>
      <button className={`btn ${on ? 'primary' : 'ghost'}`} style={{ padding: '2px 10px' }} onClick={commit}>
        {on ? '开' : '关'}
      </button>
    </div>
  );
}

/**
 * 地块的 PCG 入口。
 * 只放在「选中地块」时出现：生成必须明确作用在哪块地上，全局的一个 Generate 按钮
 * 在多地块项目里一定会误伤。顶栏那颗按钮只是这条路的快捷方式（走同样的选中逻辑）。
 */
function GeneratePanel({ parcel }: { parcel: ParcelObj }) {
  const stats = useEditor((s) => s.genStats[parcel.id]);
  const model = useEditor((s) => s.model);
  const generate = useEditor((s) => s.generateParcel);
  const clear = useEditor((s) => s.clearGenerated);

  const count = model.order.reduce((n, id) => {
    const o = model.objects[id];
    return o && o.kind === 'building' && o.generatedFrom === parcel.id ? n + 1 : n;
  }, 0);

  const pct = (v: number) => `${(v * 100).toFixed(0)}%`;

  return (
    <div className="panel">
      <h3>生成（PCG）</h3>
      <div className="gen-actions">
        <button className="btn primary" onClick={() => generate(parcel.id)}>
          生成
        </button>
        <button className="btn" onClick={() => generate(parcel.id, true)} title="换一个种子重新生成">
          换一版
        </button>
        <button className="btn ghost" onClick={() => clear(parcel.id)} disabled={count === 0}>
          清除生成
        </button>
      </div>

      {count === 0 ? (
        <div className="empty">
          尚未生成。生成只会写入本图层的 Generated 对象，手绘建筑与固定对象不会被改动。
        </div>
      ) : stats ? (
        <>
          <Row k="生成建筑" v={`${count} 栋`} />
          <Row k="覆盖率" v={`${pct(stats.coverage)} / 上限 ${pct(parcel.density)}`} />
          <Row k="容积率" v={`${stats.far.toFixed(2)} / 目标 ${parcel.far.toFixed(2)}`} />
          <Row k="最高" v={`${stats.maxHeight.toFixed(1)} m`} />
          <Row k="退线" v={`${stats.setback} m`} />
          <Row k="退线后可建" v={fmtArea(stats.buildableArea)} />
          <Row k="净可建" v={fmtArea(stats.freeArea)} />
        </>
      ) : (
        /* 撤销回到"有楼但没统计"的状态：别显示上一次的读数，那是假的 */
        <Row k="生成建筑" v={`${count} 栋（重新生成可查看统计）`} />
      )}
    </div>
  );
}

export function Inspector() {
  const model = useEditor((s) => s.model);
  const view = useEditor((s) => s.view);
  const updateObject = useEditor((s) => s.updateObject);
  const pushHistory = useEditor((s) => s.pushHistory);
  const toggleLock = useEditor((s) => s.toggleLock);
  const promoteToFixed = useEditor((s) => s.promoteToFixed);
  const deleteSelected = useEditor((s) => s.deleteSelected);
  const duplicateSelected = useEditor((s) => s.duplicateSelected);
  const patchSettings = useEditor((s) => s.patchSettings);

  const sel = view.selectionIds.map((id) => model.objects[id]).filter((o): o is SceneObject => !!o);

  const commit = (label: string, fn: () => void) => {
    pushHistory(label);
    fn();
  };

  if (sel.length === 0) {
    const counts = { site: 0, parcel: 0, building: 0, road: 0, access: 0, nobuild: 0, ground: 0 };
    for (const id of model.order) {
      const o = model.objects[id];
      if (o) counts[o.kind as keyof typeof counts]++;
    }
    return (
      <>
        <div className="panel">
          <h3>项目设置</h3>
          <Num
            label="网格间距 (m)"
            value={view.gridSize}
            step={0.5}
            digits={2}
            commit={(n) => patchSettings({ gridSize: Math.max(0.05, n) })}
          />
          <Toggle label="网格显示" on={view.gridVisible} commit={() => patchSettings({ gridVisible: !view.gridVisible })} />
          <Toggle label="网格吸附" on={view.gridSnap} commit={() => patchSettings({ gridSnap: !view.gridSnap })} />
          <Toggle label="对象吸附" on={view.snapEnabled} commit={() => patchSettings({ snapEnabled: !view.snapEnabled })} />
        </div>
        <div className="panel">
          <h3>
            选择过滤 <span className="count">Selection Filter</span>
          </h3>
          <Sel
            label="仅可选"
            value={view.selectionFilter}
            options={{
              all: '全部',
              parcels: '地块',
              buildings: '建筑',
              roads: '道路',
              ground: '地面 / 禁建',
              generated: '仅生成物',
            }}
            commit={(v) => patchSettings({ selectionFilter: v })}
          />
        </div>
        <details className="panel">
          <summary>
            模型统计 <span className="count">{model.order.length} 个对象</span>
          </summary>
          <Row k="地块" v={String(counts.parcel)} />
          <Row k="建筑" v={String(counts.building)} />
          <Row k="道路" v={String(counts.road)} />
          <Row k="出入口" v={String(counts.access)} />
          <Row k="地面" v={String(counts.ground)} />
          <Row k="禁建区" v={String(counts.nobuild)} />
        </details>
        <details className="panel">
          <summary>
            快捷键 <span className="count">Keyboard</span>
          </summary>
          <div className="empty">
            <span className="kbd">V</span> 选择 · <span className="kbd">R</span> 矩形 ·{' '}
            <span className="kbd">P</span> 多边形 · <span className="kbd">L</span> 折线 ·{' '}
            <span className="kbd">C</span> 圆 · <span className="kbd">M</span> 测量
            <br />
            <span className="kbd">F</span> 聚焦选中 · <span className="kbd">Home</span> 全览 ·{' '}
            <span className="kbd">Space</span> 拖拽平移
            <br />
            <span className="kbd">G</span> 生成地块 · <span className="kbd">Shift+G</span> 换一版
            <br />
            <span className="kbd">Shift</span> 多选 / 45° 约束 ·{' '}
            <span className="kbd">Ctrl+Z</span> 撤销 · <span className="kbd">Del</span> 删除
          </div>
        </details>
      </>
    );
  }

  if (sel.length > 1) {
    return (
      <div className="panel">
        <h3>
          多选 <span className="count">{sel.length} 个对象</span>
        </h3>
        <div className="empty" style={{ marginBottom: 8 }}>
          布尔以 <b>第一个选中</b> 的对象为主对象。<br />
          合并 / 交集会把参与对象合并进主对象；<b>减去</b> 只改主对象，被减体保留。
        </div>
        <div style={{ display: 'flex', gap: 6 }}>
          <button className="btn" onClick={duplicateSelected}>
            复制
          </button>
          <button className="btn" onClick={() => toggleLock()}>
            锁定 / 解锁
          </button>
          <button className="btn" onClick={deleteSelected}>
            删除
          </button>
        </div>
      </div>
    );
  }

  const o = sel[0];
  const paths = areaPaths(o);
  const b = paths ? boundsOfPaths(paths) : null;
  const area = paths ? pathsArea(paths) : 0;
  const per = paths ? paths.reduce((s, r) => s + ringPerimeter(r), 0) : 0;

  return (
    <>
      <div className="panel">
        <h3>
          {KIND_LABEL[o.kind]}{' '}
          <span className="count" style={{ display: 'flex', gap: 4 }}>
            <span className={`chip ${o.origin === 'generated' ? 'gen' : 'user'}`}>
              {o.origin === 'generated' ? 'Generated' : 'Fixed'}
            </span>
            {o.locked && <span className="chip lock">Locked</span>}
          </span>
        </h3>
        <div className="field">
          <label>名称</label>
          <input
            type="text"
            value={o.name}
            onChange={(e) => updateObject(o.id, { name: e.target.value } as Partial<SceneObject>)}
            onBlur={() => pushHistory('重命名')}
          />
        </div>
        <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
          <button className="btn" onClick={() => toggleLock(o.id)}>
            {o.locked ? '解锁' : '锁定'}
          </button>
          <button className="btn" onClick={() => promoteToFixed(o.id)}>
            转为固定
          </button>
          <button className="btn" onClick={deleteSelected}>
            删除
          </button>
        </div>
      </div>

      {paths && b && (
        <div className="panel">
          <h3>几何</h3>
          <Row k="面积" v={fmtArea(area)} />
          <Row k="周长" v={`${per.toFixed(2)} m`} />
          <Row k="尺寸" v={`${(b.maxX - b.minX).toFixed(2)} × ${(b.maxY - b.minY).toFixed(2)} m`} />
          <Row
            k="中心"
            v={`${ringCentroid(paths[0] ?? []).x.toFixed(1)}, ${ringCentroid(paths[0] ?? []).y.toFixed(1)}`}
          />
          <Row k="环 / 顶点" v={`${paths.length} / ${paths.reduce((s, r) => s + r.length, 0)}`} />
        </div>
      )}

      {o.kind === 'parcel' && (
        <div className="panel">
          <h3>地块参数</h3>
          <Sel
            label="用地类型"
            value={o.landUse}
            options={LANDUSE_LABEL}
            commit={(v) => commit('修改用地类型', () => updateObject(o.id, { landUse: v as LandUse } as Partial<SceneObject>))}
          />
          <Num
            label="建筑密度"
            value={o.density}
            step={0.05}
            digits={2}
            commit={(n) => commit('修改密度', () => updateObject(o.id, { density: n } as Partial<SceneObject>))}
          />
          <Num
            label="容积率"
            value={o.far}
            step={0.1}
            digits={2}
            commit={(n) => commit('修改容积率', () => updateObject(o.id, { far: n } as Partial<SceneObject>))}
          />
          <Num
            label="高度下限"
            value={o.heightRange[0]}
            commit={(n) =>
              commit('修改高度区间', () =>
                updateObject(o.id, { heightRange: [n, Math.max(n, o.heightRange[1])] } as Partial<SceneObject>),
              )
            }
          />
          <Num
            label="高度上限"
            value={o.heightRange[1]}
            commit={(n) =>
              commit('修改高度区间', () =>
                updateObject(o.id, { heightRange: [Math.min(n, o.heightRange[0]), n] } as Partial<SceneObject>),
              )
            }
          />
          <Num
            label="Seed"
            value={o.seed}
            step={1}
            digits={0}
            commit={(n) => commit('修改 Seed', () => updateObject(o.id, { seed: Math.round(n) } as Partial<SceneObject>))}
          />
        </div>
      )}

      {o.kind === 'parcel' && <GeneratePanel parcel={o} />}

      {o.kind === 'building' && (
        <div className="panel">
          <h3>建筑参数</h3>
          <Num
            label="高度 (m)"
            value={o.height}
            commit={(n) => commit('修改高度', () => updateObject(o.id, { height: n } as Partial<SceneObject>))}
          />
          <Num
            label="层数"
            value={o.floors}
            digits={0}
            commit={(n) => commit('修改层数', () => updateObject(o.id, { floors: Math.max(1, Math.round(n)) } as Partial<SceneObject>))}
          />
          <Sel
            label="风格"
            value={o.style}
            options={{ geometric: 'Geometric', semiRealistic: 'Semi-Realistic' }}
            commit={(v) => commit('修改风格', () => updateObject(o.id, { style: v as BuildingStyle } as Partial<SceneObject>))}
          />
          <Toggle
            label="固定（PCG 不再改动）"
            on={o.fixed}
            commit={() => (o.fixed ? commit('取消固定', () => updateObject(o.id, { fixed: false } as Partial<SceneObject>)) : promoteToFixed(o.id))}
          />
        </div>
      )}

      {o.kind === 'road' && (
        <div className="panel">
          <h3>道路参数</h3>
          <Sel
            label="道路等级"
            value={o.roadClass}
            options={ROAD_LABEL}
            commit={(v) => {
              const w = { primary: 12, secondary: 8, local: 6, service: 5, pedestrian: 3 }[v as RoadClass];
              commit('修改道路等级', () =>
                updateObject(o.id, { roadClass: v as RoadClass, width: w } as Partial<SceneObject>),
              );
            }}
          />
          <Num
            label="宽度 (m)"
            value={o.width}
            step={0.5}
            commit={(n) => commit('修改路宽', () => updateObject(o.id, { width: Math.max(1, n) } as Partial<SceneObject>))}
          />
          <Toggle
            label="人行道"
            on={o.sidewalk}
            commit={() => commit('人行道切换', () => updateObject(o.id, { sidewalk: !o.sidewalk } as Partial<SceneObject>))}
          />
          <Row k="中心线长度" v={`${o.centerline.reduce((s, p, i) => (i ? s + Math.hypot(p.x - o.centerline[i - 1].x, p.y - o.centerline[i - 1].y) : 0), 0).toFixed(2)} m`} />
          <Row k="控制点" v={String(o.centerline.length)} />
        </div>
      )}

      {o.kind === 'access' && (
        <div className="panel">
          <h3>出入口</h3>
          <Num
            label="开口宽度 (m)"
            value={o.width}
            step={0.5}
            commit={(n) => commit('修改开口宽度', () => updateObject(o.id, { width: Math.max(1, n) } as Partial<SceneObject>))}
          />
          <Num
            label="朝向 (°)"
            value={(o.dir * 180) / Math.PI}
            step={15}
            digits={1}
            commit={(n) =>
              commit('修改朝向', () => updateObject(o.id, { dir: (n * Math.PI) / 180 } as Partial<SceneObject>))
            }
          />
          <Sel
            label="类型"
            value={o.accessType}
            options={{ main: '主入口', service: '服务入口', pedestrian: '人行入口' }}
            commit={(v) => commit('修改出入口类型', () => updateObject(o.id, { accessType: v } as Partial<SceneObject>))}
          />
          <div className="empty" style={{ marginTop: 6 }}>
            拖动 2D 中的方向箭头可直接调整朝向（按住 Shift 吸附 45°）。
          </div>
        </div>
      )}

      {o.kind === 'ground' && (
        <div className="panel">
          <h3>
            地面 <span className="count">{SURFACE_LABEL[o.surface]}</span>
          </h3>
          <Sel
            label="表面"
            value={o.surface}
            options={SURFACE_LABEL}
            commit={(v) => commit('修改表面', () => updateObject(o.id, { surface: v as GroundSurface } as Partial<SceneObject>))}
          />
          <Row k="铺设面积" v={fmtArea(Math.abs(area))} />
          {o.generatedFrom && (
            <div className="empty" style={{ marginTop: 6 }}>
              这块地面由地块「{model.objects[o.generatedFrom]?.name ?? o.generatedFrom}」生成。
              改表面类型会保留；重新生成时会被覆盖。
            </div>
          )}
        </div>
      )}

      {isAreaObj(o) && (
        <div className="panel">
          <h3>对象操作</h3>
          <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            <button className="btn" onClick={duplicateSelected}>
              复制 (Ctrl+D)
            </button>
            <button className="btn" onClick={() => useEditor.getState().applyOffset(-3)}>
              内缩 3m
            </button>
            <button className="btn" onClick={() => useEditor.getState().applyOffset(3)}>
              外扩 3m
            </button>
          </div>
        </div>
      )}
    </>
  );
}
