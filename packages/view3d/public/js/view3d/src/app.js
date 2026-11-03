// 三维场景查看器：Plan IR 的四个几何通道 → SceneIR → three。
//
// 和 packages/editor-web（平面规则验证器）的分工：
//   editor-web —— 2D，看**规则**：R1–R11、ready 判定、楼位切分是否合规。抽象，但可判。
//   view3d     —— 3D，看**结果**：这块地在这个生成器下会长成什么样。
// 两者不是替代关系。3D 看不出一条边算不算"沿边贴合"，2D 也看不出"这条街两边是什么"。
//
// F3 之后这个文件里**没有任何图元代码**了：几何在 `realizer.ts`，规则在 `scene-ir`。
// 它只剩三件事：拿数据、接线、显示数字。
import { buildSceneIR, irKey } from "../../scene-ir/src/index.js";
import { LAYER_INFO } from "../../scene-ir/src/palette.js";
import { Realizer } from "./realizer.js";
import { Stage } from "./stage.js";
import { ringBounds } from "./geom.js";
import { ZONE_LABEL } from "../../plan-ir/src/types.js";
const DATA = window.__SCENES__;
/** 图层开关。`base` 不给开关：那是输入轮廓，关掉就不知道该看哪儿了。 */
const TOGGLES = ['surfaces', 'streets', 'zoning', 'massing', 'props', 'markings'].map((key) => ({ key }));
/** 五个视图。正交四个是"量"用的，透视那个是"看"用的 —— 保留它只为对照，不作为判据。 */
const VIEWS = [
    { p: 'iso', label: '等轴', hint: '正交 · 方位 45° / 仰角 38° —— 主视图，量高度用这个' },
    { p: 'top', label: '顶视', hint: '正交 · 正上方 —— 看楼位怎么排在宗地里' },
    { p: 'front', label: '正视', hint: '正交 · 自 +Z 看 —— 看天际线与层数差' },
    { p: 'right', label: '右视', hint: '正交 · 自 +X 看 —— 和正视配对看两个方向' },
    { p: 'persp', label: '透视', hint: '透视 —— 只做观感对照。**别用它量高度**：远处 30 m 会被看成 20 m' },
];
const el = {
    list: () => document.getElementById('list'),
    stage: () => document.getElementById('stage'),
    toggleBox: () => document.getElementById('toggles'),
    info: () => document.getElementById('info'),
    picked: () => document.getElementById('picked'),
    views: () => document.getElementById('views'),
    meta: () => document.getElementById('meta'),
    search: () => document.getElementById('search'),
};
const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
class App {
    stage;
    realizer;
    toggles = {
        base: true,
        surfaces: true,
        streets: true,
        zoning: true,
        massing: true,
        props: true,
        markings: true,
    };
    current = null;
    ir = null;
    /** nodeId → 节点。拾取面板读它，而不是读 mesh.userData —— 重建后 mesh 换了，节点不会。 */
    byId = new Map();
    /** 手动改过的层数（编辑回路的输入）。换方案时清空。 */
    override = {};
    selected = null;
    realize = { built: 0, reused: 0, removed: 0, total: 0, ms: 0 };
    query = '';
    constructor() {
        this.stage = new Stage(el.stage());
        this.realizer = new Realizer();
        this.stage.root.add(this.realizer.group);
        // 有楼块的排前面：一打开就想看到"有东西"的场景
        const sorted = [...DATA.scenes].sort((a, b) => {
            const za = a.plan.zoning.length > 0 ? 0 : 1;
            const zb = b.plan.zoning.length > 0 ? 0 : 1;
            if (za !== zb)
                return za - zb;
            if (a.generator !== b.generator)
                return a.generator.localeCompare(b.generator);
            return a.id.localeCompare(b.id);
        });
        DATA.scenes = sorted;
        const withZoning = sorted.filter((s) => s.plan.zoning.length > 0).length;
        el.meta().textContent = `${sorted.length} 份方案 · ${withZoning} 份带宗地 · 数据打包于 ${DATA.generatedAt}`;
        this.renderToggles();
        this.renderViews();
        this.renderList();
        this.renderPicked();
        el.search().addEventListener('input', (e) => {
            this.query = e.target.value.trim().toLowerCase();
            this.renderList();
        });
        this.bindPick();
        const first = sorted[0];
        if (first)
            this.select(first.id);
        // 调试钩子：无头浏览器验页面时要从外面读相机/场景状态，
        // 光看截图判断不了"没渲染"和"渲染了但取景偏了"。
        window.__APP__ = this;
        window.__STAGE__ = this.stage;
        window.__REALIZER__ = this.realizer;
    }
    /** 给外部探针用：当前相机、场景规模、增量计数。 */
    debugState() {
        const c = this.stage.camera;
        return {
            camera: [c.position.x, c.position.y, c.position.z],
            preset: this.stage.preset,
            ortho: c.isOrthographicCamera === true,
            nodes: this.realizer.nodeCount,
            buildings: this.realizer.pickables().length,
            selected: this.selected,
            realize: this.realize,
            frames: this.stage.frames,
            ir: this.ir ? irKey(this.ir).length : 0,
        };
    }
    /** IR 指纹：同一份方案两次打开必须一致（确定性验收口径）。 */
    keyDigest() {
        return this.ir ? irKey(this.ir) : '';
    }
    get buildingCount() {
        return this.realizer.pickables().length;
    }
    /** 楼块世界包围盒（探针用；拿到的是**当前**这块几何，重建后也准）。 */
    boxOf(id) {
        return this.realizer.nodeBox(id);
    }
    /**
     * 第 i 栋楼在屏幕上的位置（CSS 像素）。
     * 无头验页面时用它去点**真实坐标** —— 绕过交互直接调内部方法就测不到"点得中"。
     */
    screenOf(i) {
        const m = this.realizer.pickables()[i];
        if (!m)
            return null;
        const box = this.boxOf(m.userData.nodeId);
        if (!box)
            return null;
        const v = new THREE.Vector3((box.min.x + box.max.x) / 2, box.min.y + (box.max.y - box.min.y) * 0.6, (box.min.z + box.max.z) / 2).project(this.stage.camera);
        const r = this.stage.renderer.domElement.getBoundingClientRect();
        return { x: r.left + ((v.x + 1) / 2) * r.width, y: r.top + ((1 - v.y) / 2) * r.height };
    }
    /**
     * 量高对账（F2 的 Gate）。走三条互相独立的路径，缺一条就测不出真问题：
     *
     *   a) 标签路径：`floors × 层高`
     *   b) 几何路径：mesh 包围盒的真实高度
     *   c) 屏幕路径：把底/顶投到画布上数像素
     *
     * 两个坑：
     *   · 正交下"1 米 = 多少像素"对**竖直方向**不是 `画布高 / (2×视野半高)`，
     *     而是再乘 `cos(仰角)` —— 仰角 38° 时竖直方向被压到 0.79 倍。
     *     第一版按前者算，误差稳定在 21%，看着像渲染错了，其实是量法错了。
     *   · 只能用单位竖直线在**该楼的屏幕上位置**现场标定，不能拿屏幕中心标定：
     *     正交下处处相等，但透视下不等，写死会让透视视图的数字偷偷偏掉。
     */
    measure() {
        const id = this.selected ?? this.realizer.pickables()[0]?.userData?.nodeId;
        if (!id)
            return null;
        const box = this.boxOf(id);
        const n = this.byId.get(id);
        if (!box || !n || n.type !== 'extrude')
            return null;
        const cx = (box.min.x + box.max.x) / 2;
        const cz = (box.min.z + box.max.z) / 2;
        const cam = this.stage.camera;
        const H = this.stage.canvasHeight;
        const project = (y) => new THREE.Vector3(cx, y, cz).project(cam);
        const screenY = (v) => ((1 - v.y) / 2) * H;
        const y0 = box.min.y;
        const y1 = box.max.y;
        const geomH = y1 - y0;
        const pxPerMeter = Math.abs(screenY(project(y0 + 1)) - screenY(project(y0)));
        const px = Math.abs(screenY(project(y1)) - screenY(project(y0)));
        const p = n.props;
        const floors = p.floors ?? 0;
        const floorHeight = p.floorHeight ?? 0;
        const declared = p.height ?? geomH;
        const wantData = floors * floorHeight;
        const wantPx = geomH * pxPerMeter;
        return {
            id,
            kind: p.kind ?? '?',
            floors,
            floorHeight,
            declared,
            geomH,
            px,
            pxPerMeter,
            errData: wantData > 0 ? (Math.abs(geomH - wantData) / wantData) * 100 : 999,
            errPx: wantPx > 0 ? (Math.abs(px - wantPx) / wantPx) * 100 : 999,
        };
    }
    // ───────────────────────────── 编辑回路（F3 的验收动作）
    //
    // 改一栋楼的层数 → 只有那个 `bld:` 节点的 geomKey 变了 → realizer 只重建它一个。
    // 这就是"拖一个角点只重建 1~2 个节点"的同一条代码路径。
    /** 设定某栋楼的层数；传 null 恢复自动值。 */
    setFloors(id, floors) {
        if (floors == null)
            delete this.override[id];
        else
            this.override[id] = Math.max(1, Math.round(floors));
        this.rebuild(false);
    }
    renderToggles() {
        el.toggleBox().innerHTML = TOGGLES.map((t) => {
            const info = LAYER_INFO[t.key];
            return `<label class="tg" title="${esc(info.hint)}">
        <input type="checkbox" data-k="${t.key}" ${this.toggles[t.key] ? 'checked' : ''}/>
        <span>${esc(info.label)}</span>
      </label>`;
        }).join('');
        el.toggleBox().querySelectorAll('input').forEach((inp) => {
            inp.addEventListener('change', () => {
                const k = inp.dataset.k;
                this.toggles[k] = inp.checked;
                this.rebuild(false);
            });
        });
    }
    /** 切视图 + 同步按钮高亮。外部探针也走这个 —— 否则会出现"状态变了、按钮还亮着上一个"的假象。 */
    setPreset(p) {
        this.stage.setPreset(p);
        this.renderViews();
    }
    renderViews() {
        el.views().innerHTML = VIEWS.map((v) => `<button class="vbtn${this.stage.preset === v.p ? ' on' : ''}" data-p="${v.p}" title="${esc(v.hint)}">${esc(v.label)}</button>`).join('');
        el.views().querySelectorAll('button').forEach((b) => {
            b.addEventListener('click', () => this.setPreset(b.dataset.p));
        });
    }
    /** 点画布选楼。拖动超过 5 px 算转视角，不算点击 —— 否则每次转完都会误选一栋。 */
    bindPick() {
        const cv = this.stage.renderer.domElement;
        let down = null;
        cv.addEventListener('pointerdown', (e) => {
            down = { x: e.clientX, y: e.clientY };
        });
        cv.addEventListener('pointerup', (e) => {
            const d = down;
            down = null;
            if (!d || e.button !== 0)
                return;
            if (Math.hypot(e.clientX - d.x, e.clientY - d.y) > 5)
                return;
            const hit = this.stage.pick(e.clientX, e.clientY, this.realizer.pickables());
            this.selectBuilding(hit ? (hit.userData?.nodeId ?? null) : null);
        });
    }
    selectBuilding(id) {
        this.selected = id;
        this.realizer.setSelection(id);
        this.stage.markDirty();
        this.stage.renderNow();
        this.renderPicked();
    }
    renderList() {
        const q = this.query;
        const hits = DATA.scenes.filter((s) => !q || s.id.toLowerCase().includes(q) || s.generator.toLowerCase().includes(q));
        let html = '';
        let lastGen = '';
        for (const s of hits) {
            if (s.generator !== lastGen) {
                lastGen = s.generator;
                html += `<div class="gen">${esc(s.generator)}</div>`;
            }
            const hasMass = s.plan.zoning.length > 0;
            const active = this.current?.id === s.id ? ' active' : '';
            html += `<button class="item${active}" data-id="${esc(s.id)}" title="${esc(s.id)}">
        <span class="dot ${s.ready ? 'ok' : 'bad'}"></span>
        <span class="nm">${esc(s.id.replace(/^city24-s\d+-/, ''))}</span>
        ${hasMass ? '<span class="tag">楼</span>' : ''}
      </button>`;
        }
        el.list().innerHTML = html || '<div class="empty">没有匹配的</div>';
        el.list().querySelectorAll('button').forEach((b) => {
            b.addEventListener('click', () => this.select(b.dataset.id));
        });
    }
    select(id) {
        const s = DATA.scenes.find((x) => x.id === id);
        if (!s)
            return;
        this.current = s;
        // 换方案 = 换世界：手动改的层数和选中态都失效
        this.override = {};
        this.selected = null;
        this.realizer.setSelection(null);
        this.renderList();
        this.rebuild(true);
    }
    rebuild(refit) {
        const s = this.current;
        if (!s)
            return;
        this.ir = buildSceneIR(s.plan, s.ring, {
            toggles: this.toggles,
            seedKey: `${s.generator}:${s.id}`,
            floors: this.override,
        });
        this.byId = new Map(this.ir.nodes.map((n) => [n.id, n]));
        this.realize = this.realizer.update(this.ir);
        // 关掉"楼块"层会把选中的那栋从 IR 里删掉 —— 选中态必须跟着失效，
        // 否则面板会显示一栋已经不存在的楼（读出来全是"—"）。
        if (this.selected && !this.byId.has(this.selected)) {
            this.selected = null;
            this.realizer.setSelection(null);
        }
        if (refit) {
            const b = ringBounds([s.ring, ...s.plan.surfaces.map((x) => x.ring)]);
            if (b) {
                this.stage.frame({ min: [b.min[0], 0, b.min[1]], max: [b.max[0], Math.max(12, this.ir.stats.tallest), b.max[1]] });
            }
        }
        this.stage.markDirty();
        this.stage.renderNow();
        this.renderInfo(s);
        this.renderPicked();
    }
    renderInfo(s) {
        const st = this.ir?.stats;
        if (!st)
            return;
        const hasZoning = s.plan.zoning.length > 0;
        const rz = this.realize;
        const rows = [
            ['生成器', `<code>${esc(s.generator)}</code>`],
            ['引擎判定', s.ready ? '<b class="ok">ready</b>' : '<b class="bad">not ready</b>'],
            ['地块面积', `${Math.round(s.area).toLocaleString()} m²`],
            ['地面', `${st.surfaces} 块`],
            ['街道', `${st.nets} 条${st.longestStreet ? ` · 最长 ${st.longestStreet.toFixed(0)} m` : ''}`],
            ['物件', `${st.objects} 个${st.markings ? `（标线 ${st.markings}）` : ''}`],
            ['楼块', hasZoning ? `${st.slots} 个 · 占地 ${Math.round(st.footArea).toLocaleString()} m²` : '—'],
            ['最高', hasZoning ? `${st.tallest.toFixed(1)} m` : '—'],
            ['场景节点', `${rz.total} 个`],
            ['本次更新', `<b>新建 ${rz.built}</b> · 复用 ${rz.reused} · 删 ${rz.removed} · ${rz.ms.toFixed(1)} ms`],
        ];
        // 画面空不能只让我们自己知道原因 —— 把"为什么空"写到面板上。
        const notes = [];
        if (!hasZoning) {
            notes.push(`这份方案引擎没有输出宗地（<code>Plan.zoning</code> 为空），所以没有楼块。
         不是渲染漏了 —— 是这块地本来就没进 zoning 通道。`);
        }
        if (s.plan.surfaces.length === 0) {
            notes.push(`引擎也没给地面（<code>Plan.surfaces</code> 为空），所以这块地看起来是空的。
         公园族就是这样：能出树和步道，不出草地底（台账 Q-008 已记）。`);
        }
        el.info().innerHTML = `
      <h2>${esc(s.id)}</h2>
      <table class="kv">${rows.map(([k, v]) => `<tr><th>${k}</th><td>${v}</td></tr>`).join('')}</table>
      ${notes.map((n) => `<p class="warn">${n}</p>`).join('')}
      <p class="mut">勾掉一个图层只会增删那一层的节点，<b>不会重建整场</b> —— 看"本次更新"那一行。</p>`;
    }
    renderPicked() {
        const id = this.selected;
        if (!id) {
            el.picked().innerHTML = `<div class="empty">点画布里的楼块${this.buildingCount ? '' : '（这份方案没有楼块）'}</div>`;
            return;
        }
        const n = this.byId.get(id);
        const p = (n && n.type === 'extrude' ? n.props : {});
        const kind = p.kind;
        const floors = p.floors ?? 0;
        const h = p.height ?? 0;
        const fmin = p.fmin ?? 1;
        const fmax = p.fmax ?? 20;
        const overridden = p.overridden === true;
        const rows = [
            ['用途', kind ? `${esc(ZONE_LABEL[kind] ?? kind)} <code>${esc(kind)}</code>` : '—'],
            ['层数', `${floors} 层${overridden ? ` <span class="tag2">改过，自动值 ${p.auto}</span>` : ''}`],
            ['层高', `${p.floorHeight ? p.floorHeight.toFixed(2) : '—'} m`],
            ['总高', `<b>${h.toFixed(2)} m</b>`],
            ['面宽 × 进深', `${(p.w ?? 0).toFixed(1)} × ${(p.d ?? 0).toFixed(1)} m`],
            ['楼位 id', `<code>${esc(String(p.id ?? '?'))}</code>`],
        ];
        el.picked().innerHTML = `
      <table class="kv">${rows.map(([k, v]) => `<tr><th>${k}</th><td>${v}</td></tr>`).join('')}</table>
      <div class="slider">
        <label for="fl">层数</label>
        <input id="fl" type="range" min="${fmin}" max="${fmax}" step="1" value="${Math.min(fmax, Math.max(fmin, floors))}"/>
        <b id="flv">${floors}</b>
      </div>
      ${overridden ? '<button id="flreset" class="mini">恢复自动</button>' : ''}
      <p class="mut">拖上面的滑块 —— 改的只有这一栋，<b>别的节点一个都不重建</b>；
        右栏"本次更新"会显示 <code>新建 1 · 复用 N</code>。</p>`;
        const slider = document.getElementById('fl');
        if (slider) {
            // input 事件：拖动过程中连续触发，正好用来观察"每次只重建 1 个"
            slider.addEventListener('input', () => {
                const v = Number(slider.value);
                const b = document.getElementById('flv');
                if (b)
                    b.textContent = String(v);
                this.setFloors(id, v);
            });
        }
        const reset = document.getElementById('flreset');
        if (reset)
            reset.addEventListener('click', () => this.setFloors(id, null));
    }
}
try {
    new App();
}
catch (e) {
    const box = document.getElementById('stage');
    if (box)
        box.innerHTML = `<pre class="fatal">${esc(String(e?.stack ?? e))}</pre>`;
    throw e;
}
