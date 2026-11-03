/**
 * 3D 视图外壳（PRD §5：3D = Result）
 *
 * 这里只做三件事：挂载/卸载（不销毁）舞台、视图预设按钮、结果读数。
 * 任何编辑动作都不该出现在这个文件里 —— 3D 是「看结果」的地方。
 *
 * WebGL 可能不可用（虚拟机 / 无显卡 / 沙箱浏览器的极端情况），
 * 所以舞台创建必须可失败、可降级，不能把整个编辑器拖成白屏。
 */
import { useEffect, useRef, useState } from 'react';
import { Stage3D as Stage, getStage3D, PRESET_LABEL, ViewPreset } from '../editor/three/stage3d';
import { useEditor } from '../editor/store';
import { fmtArea } from '../editor/render';

const PRESETS: ViewPreset[] = ['iso', 'top', 'front', 'right', 'persp'];

const EMPTY_STATS: Stage['stats'] = { buildings: 0, generated: 0, fixed: 0, footprintArea: 0, maxHeight: 0, nodes: 0 };

export function Stage3D() {
  const host = useRef<HTMLDivElement>(null);
  const stage = useRef<Stage | null>(null);
  const [preset, setPreset] = useState<ViewPreset>('iso');
  const [stats, setStats] = useState(EMPTY_STATS);
  const [err, setErr] = useState<string | null>(null);

  const selectionIds = useEditor((s) => s.view.selectionIds);
  const objects = useEditor((s) => s.model.objects);
  const selName = selectionIds.length === 1 ? objects[selectionIds[0]]?.name : undefined;

  useEffect(() => {
    let s: Stage;
    try {
      s = getStage3D();
    } catch (e) {
      setErr(e instanceof Error ? e.message : 'WebGL 初始化失败');
      return;
    }
    stage.current = s;
    s.onStats = (next) => setStats({ ...next });
    if (host.current) s.attach(host.current);
    setStats({ ...s.stats });
    setPreset(s.getPreset());
    return () => {
      s.onStats = undefined;
      s.detach();
      stage.current = null;
    };
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return;
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      const s = stage.current;
      if (!s) return;
      const idx = '12345'.indexOf(e.key);
      if (idx >= 0) {
        const p = PRESETS[idx];
        s.setPreset(p);
        setPreset(p);
        e.preventDefault();
        return;
      }
      if (e.key === 'f' || e.key === 'F') {
        const id = useEditor.getState().view.selectionIds[0];
        if (id) s.focusObject(id);
        else s.frameAll();
        e.preventDefault();
        return;
      }
      if (e.key === 'Home' || e.key === '0') {
        s.frameAll();
        e.preventDefault();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  if (err) {
    return (
      <div className="stage3d">
        <div className="stage3d-empty">
          <h3>3D 预览不可用</h3>
          <p>当前环境未能创建 WebGL 上下文：{err}</p>
          <p>2D 编辑不受影响；导出 JSON 与所有几何计算照常工作。</p>
        </div>
      </div>
    );
  }

  const pick = (p: ViewPreset) => {
    stage.current?.setPreset(p);
    setPreset(p);
  };

  return (
    <div className="stage3d">
      <div className="stage3d-host" ref={host} />

      <div className="stage3d-hud tl">
        <div className="seg tight">
          {PRESETS.map((p) => (
            <button key={p} className={preset === p ? 'on' : ''} onClick={() => pick(p)} title={`${PRESET_LABEL[p]}视图`}>
              {PRESET_LABEL[p]}
            </button>
          ))}
        </div>
        <button className="btn ghost sm" onClick={() => stage.current?.frameAll()} title="全览（Home）">
          全览
        </button>
        <button
          className="btn ghost sm"
          disabled={!selectionIds.length}
          onClick={() => {
            const id = selectionIds[0];
            if (id) stage.current?.focusObject(id);
          }}
          title="聚焦选中对象（F）"
        >
          聚焦
        </button>
      </div>

      <div className="stage3d-hud tr">
        <div className="stat">
          建筑 <b>{stats.buildings}</b>
        </div>
        <div className="stat">
          最高 <b>{stats.maxHeight ? `${stats.maxHeight.toFixed(1)} m` : '—'}</b>
        </div>
        <div className="stat">
          基底 <b>{fmtArea(stats.footprintArea)}</b>
        </div>
        <div className="stat dim">
          固定 {stats.fixed} · 生成 {stats.generated}
        </div>
      </div>

      {stats.nodes === 0 && (
        <div className="stage3d-empty">
          <h3>场景为空</h3>
          <p>回到 2D 画一个地块或建筑，这里会立刻出现结果。</p>
        </div>
      )}

      {selName && <div className="stage3d-sel">已选：{selName}</div>}

      <div className="stage3d-hud bl">左键旋转 · 右键平移 · 滚轮缩放 · 双击聚焦 · 1–5 切视图</div>
    </div>
  );
}
