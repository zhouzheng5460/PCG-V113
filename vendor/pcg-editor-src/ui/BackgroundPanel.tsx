import { useRef, useState } from 'react';
import { useEditor } from '../editor/store';
import { loadImage } from '../editor/imageCache';

const OPACITIES = [0, 0.25, 0.5, 0.75, 1];

/** 读文件为 dataURL 并取原始像素尺寸 */
function readImage(file: File): Promise<{ src: string; width: number; height: number }> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = async () => {
      const src = String(reader.result);
      try {
        const img = await loadImage(src);
        resolve({ src, width: img.naturalWidth, height: img.naturalHeight });
      } catch (e) {
        reject(e);
      }
    };
    reader.onerror = () => reject(new Error('读取文件失败'));
    reader.readAsDataURL(file);
  });
}

export function BackgroundPanel() {
  const model = useEditor((s) => s.model);
  const view = useEditor((s) => s.view);
  const calib = useEditor((s) => s.calib);
  const setBackground = useEditor((s) => s.setBackground);
  const updateBackground = useEditor((s) => s.updateBackground);
  const removeBackground = useEditor((s) => s.removeBackground);
  const startCalibration = useEditor((s) => s.startCalibration);
  const cancelCalibration = useEditor((s) => s.cancelCalibration);
  const applyCalibration = useEditor((s) => s.applyCalibration);
  const patchSettings = useEditor((s) => s.patchSettings);
  const showToast = useEditor((s) => s.showToast);

  const inputRef = useRef<HTMLInputElement>(null);
  const [meters, setMeters] = useState('20');
  const bg = model.background;

  const onPick = async (f: File | undefined) => {
    if (!f) return;
    if (!/^image\/(png|jpeg|webp)$/.test(f.type)) {
      showToast('只支持 PNG / JPG / WEBP');
      return;
    }
    try {
      const { src, width, height } = await readImage(f);
      setBackground(src, width, height);
    } catch {
      showToast('背景图读取失败');
    }
  };

  return (
    <div className="panel">
      <h3>
        背景图 <span className="count">Reference</span>
      </h3>

      <input
        ref={inputRef}
        type="file"
        accept="image/png,image/jpeg,image/webp"
        style={{ display: 'none' }}
        onChange={(e) => {
          void onPick(e.target.files?.[0]);
          e.currentTarget.value = '';
        }}
      />

      {!bg && (
        <>
          <button className="btn" style={{ width: '100%', justifyContent: 'center' }} onClick={() => inputRef.current?.click()}>
            导入总平面图
          </button>
          <div className="empty" style={{ marginTop: 8 }}>
            PNG / JPG / WEBP。导入后会进入<span style={{ color: 'var(--text)' }}>两点标定</span>：
            在图上点两个已知距离的点，输入实际米数，其余绘图自动进入米制坐标。
          </div>
        </>
      )}

      {bg && (
        <>
          <div className="row">
            <span className="k">图片尺寸</span>
            <span className="v">
              {bg.widthPx} × {bg.heightPx} px
            </span>
          </div>
          <div className="row">
            <span className="k">比例</span>
            <span className="v">1px = {(bg.metersPerPixel * 100).toFixed(2)} cm</span>
          </div>
          <div className="row">
            <span className="k">覆盖范围</span>
            <span className="v">
              {(bg.widthPx * bg.metersPerPixel).toFixed(1)} × {(bg.heightPx * bg.metersPerPixel).toFixed(1)} m
            </span>
          </div>

          <div className="field" style={{ marginTop: 6 }}>
            <label>透明度</label>
            <span className="v" style={{ color: 'var(--dim)' }}>
              {Math.round(bg.opacity * 100)}%
            </span>
          </div>
          <div className="mini-grid" style={{ gridTemplateColumns: 'repeat(5, 1fr)' }}>
            {OPACITIES.map((o) => (
              <button
                key={o}
                className={`mini ${Math.abs(bg.opacity - o) < 0.01 ? 'on' : ''}`}
                onClick={() => updateBackground({ opacity: o }, '背景透明度')}
                title={`透明度 ${o * 100}%`}
              >
                {o * 100}
              </button>
            ))}
          </div>

          <div className="field" style={{ marginTop: 8 }}>
            <label>旋转</label>
            <input
              type="range"
              min={-180}
              max={180}
              step={0.5}
              value={(bg.rotation * 180) / Math.PI}
              onChange={(e) =>
                updateBackground({ rotation: (Number(e.target.value) * Math.PI) / 180 }, '旋转背景图')
              }
              style={{ width: 118 }}
            />
          </div>
          <div className="row">
            <span className="k" />
            <span className="v">{((bg.rotation * 180) / Math.PI).toFixed(1)}°</span>
          </div>

          <div style={{ display: 'flex', gap: 6, marginTop: 8, flexWrap: 'wrap' }}>
            <button
              className={`btn ${bg.locked ? 'primary' : ''}`}
              onClick={() => updateBackground({ locked: !bg.locked }, bg.locked ? '解锁背景' : '锁定背景')}
            >
              {bg.locked ? '已锁定' : '锁定'}
            </button>
            <button className="btn" onClick={() => updateBackground({ visible: !bg.visible }, '显示切换')}>
              {bg.visible ? '隐藏' : '显示'}
            </button>
            <button className="btn" onClick={() => inputRef.current?.click()}>
              换图
            </button>
            <button className="btn" onClick={removeBackground}>
              删除
            </button>
          </div>

          <div className="field" style={{ marginTop: 10 }}>
            <label>拖动编辑</label>
            <button
              className={`btn ${view.bgEditing ? 'primary' : 'ghost'}`}
              style={{ padding: '2px 10px' }}
              disabled={bg.locked}
              onClick={() => patchSettings({ bgEditing: !view.bgEditing })}
            >
              {view.bgEditing ? '开' : '关'}
            </button>
          </div>
          {!bg.locked && (
            <div className="empty" style={{ marginTop: 4 }}>
              画布上拖动＝移动 · <span className="kbd">Alt</span>＋拖动＝旋转 ·{' '}
              <span className="kbd">Shift</span>＋拖动＝缩放
            </div>
          )}

          {/* ---------------- 两点标定 ---------------- */}
          <div style={{ borderTop: '1px solid var(--border-soft)', marginTop: 10, paddingTop: 9 }}>
            <div className="field">
              <label>比例标定</label>
              {calib ? (
                <button className="btn ghost" style={{ padding: '2px 8px' }} onClick={cancelCalibration}>
                  取消
                </button>
              ) : (
                <button className="btn" style={{ padding: '2px 8px' }} onClick={startCalibration}>
                  两点标定
                </button>
              )}
            </div>

            {calib && (
              <>
                <div className="empty" style={{ margin: '6px 0' }}>
                  在背景图上点两个已知距离的点（例如一段 20m 的尺寸线两端）。
                  <br />
                  状态：{calib.a ? (calib.b ? 'A、B 已就绪' : '已点 A，请点 B') : '请点 A'}
                </div>
                <div className="field">
                  <label>实际距离 (m)</label>
                  <input
                    data-testid="calib-meters"
                    type="number"
                    step={0.5}
                    value={meters}
                    onFocus={(e) => e.currentTarget.select()}
                    onChange={(e) => setMeters(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') applyCalibration(Number(meters));
                    }}
                  />
                </div>
                <button
                  data-testid="calib-apply"
                  className="btn primary"
                  style={{ width: '100%', justifyContent: 'center', marginTop: 6 }}
                  disabled={!calib.a || !calib.b}
                  onClick={() => applyCalibration(Number(meters))}
                >
                  应用标定
                </button>
              </>
            )}

            {!calib && bg.locked && (
              <div className="empty" style={{ marginTop: 4 }}>
                背景已锁定，可安全描图。需要重新标定请点「两点标定」。
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}
