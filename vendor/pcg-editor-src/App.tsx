import { Suspense, lazy } from 'react';
import { Canvas2D } from './ui/Canvas2D';
import { BackgroundPanel } from './ui/BackgroundPanel';
import { Inspector } from './ui/Inspector';
import { Layers } from './ui/Layers';
import { StatusBar } from './ui/StatusBar';
import { Toolbar } from './ui/Toolbar';
import { useEditor } from './editor/store';

// three.js 近 600 kB：只在真的切到 3D 时才拉，别拖慢 2D 首屏
const Stage3D = lazy(() => import('./ui/Stage3D').then((m) => ({ default: m.Stage3D })));

function TopBar() {
  const model = useEditor((s) => s.model);
  const view = useEditor((s) => s.view);
  const patchSettings = useEditor((s) => s.patchSettings);
  const setModel = useEditor((s) => s.setModel);
  const pushHistory = useEditor((s) => s.pushHistory);
  const newProject = useEditor((s) => s.newProject);
  const loadDemo = useEditor((s) => s.loadDemo);
  const showToast = useEditor((s) => s.showToast);
  const generateParcel = useEditor((s) => s.generateParcel);
  const parcels = useEditor((s) => s.model.order.filter((id) => s.model.objects[id]?.kind === 'parcel').length);

  const exportJson = () => {
    const blob = new Blob([JSON.stringify(model, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${model.name || 'project'}.json`;
    a.click();
    URL.revokeObjectURL(url);
    showToast('Project JSON 已导出');
  };

  return (
    <div className="topbar">
      <div className="brand">
        <div className="brand-mark" />
        Context City PCG
        <small>2D Spatial Authoring</small>
      </div>

      <input
        className="project-name"
        value={model.name}
        onChange={(e) => setModel({ ...model, name: e.target.value })}
        onBlur={() => pushHistory('重命名项目')}
        spellCheck={false}
      />

      <div className="spacer" />

      <div className="seg">
        <button className={view.activeView === '2d' ? 'on' : ''} onClick={() => patchSettings({ activeView: '2d' })}>
          2D Plan
        </button>
        <button className={view.activeView === '3d' ? 'on' : ''} onClick={() => patchSettings({ activeView: '3d' })}>
          3D Preview
        </button>
      </div>

      <button className="btn ghost" onClick={loadDemo} title="载入示例园区">
        示例
      </button>
      <button className="btn ghost" onClick={newProject} title="清空项目">
        新建
      </button>
      <button className="btn" onClick={exportJson} title="导出 Project JSON">
        导出 JSON
      </button>
      <button
        className="btn primary"
        onClick={(e) => generateParcel(undefined, e.shiftKey)}
        disabled={parcels === 0}
        title={
          parcels === 0
            ? '先画一个地块（P）再生成'
            : '对选中（或唯一）地块做 PCG 生成 · Shift 点击 = 换一版'
        }
      >
        Generate
      </button>
    </div>
  );
}

export default function App() {
  const activeView = useEditor((s) => s.view.activeView);
  return (
    <div className="app">
      <TopBar />
      <div className="main">
        <Toolbar />
        {activeView === '2d' ? (
          <Canvas2D />
        ) : (
          <Suspense fallback={<div className="stage3d-loading">正在加载 3D 渲染器…</div>}>
            <Stage3D />
          </Suspense>
        )}
        <div className="side">
          <Inspector />
          <BackgroundPanel />
          <Layers />
        </div>
      </div>
      <StatusBar />
    </div>
  );
}
