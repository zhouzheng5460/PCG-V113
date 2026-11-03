// 画布：把一份地块 + 结算结果画成 SVG，同时吐出一份**句柄表**。
//
// 为什么句柄要和 SVG 一起返回：命中测试和绘制必须用同一份坐标。
// 分开算的话，只要有一处少了个 padding，用户就会发现"看着在这、拖的是那"。
//
// 图层顺序（从下到上）：底色 → 街道带 → 宗地 → 楼位 → 贴合点 → 轮廓 → 句柄
// 轮廓压在楼位之上，是因为拖动时最需要看清的就是地块边界。
import { kindColor } from "../../demo/src/render.js";
import { boundsOf, polyPath, STREET_FILL, View } from "./view.js";
const r1 = (n) => n.toFixed(1);
function worldAxisAlignedGrid(bb, step) {
    const out = [];
    const x0 = Math.floor(bb.minX / step) * step;
    const y0 = Math.floor(bb.minY / step) * step;
    for (let x = x0; x <= bb.maxX + step; x += step) {
        out.push(`<line data-wx="${x}" data-wy="0" class="gridline-v"/>`);
    }
    for (let y = y0; y <= bb.maxY + step; y += step) {
        out.push(`<line data-wx="0" data-wy="${y}" class="gridline-h"/>`);
    }
    return out;
}
export function drawCanvas(inp, targetW, fixedView) {
    const all = [...inp.ring];
    for (const s of inp.streets)
        all.push(s.a, s.b);
    for (const z of inp.zoning ?? [])
        all.push(...z.quad);
    for (const sl of inp.slots)
        all.push(...sl.quad);
    // 拖动期间必须锁住视图：否则顶点一飞出去 bbox 就变，整张图重新缩放，
    // 手柄会从鼠标底下跑掉 —— 摸起来像"拖不动"。
    const view = fixedView ?? new View(boundsOf(all), 30, targetW);
    const { W, H } = view;
    const o = [];
    o.push(`<svg id="cv" viewBox="0 0 ${W} ${H}" width="100%" preserveAspectRatio="xMidYMid meet">`);
    o.push(`<rect x="0" y="0" width="${W}" height="${H}" fill="#15151a" rx="10"/>`);
    // ── 8 m 模数网格：切楼位用的就是它，能直观看出"为什么这块只切出 1 个位"
    if (inp.showGrid) {
        const step = 8;
        const g = [];
        const x0 = Math.floor(view.bb.minX / step) * step;
        const x1 = Math.ceil(view.bb.maxX / step) * step;
        const y0 = Math.floor(view.bb.minY / step) * step;
        const y1 = Math.ceil(view.bb.maxY / step) * step;
        for (let x = x0; x <= x1; x += step) {
            g.push(`M${r1(view.sx(x))} ${r1(view.sy(y0))} L${r1(view.sx(x))} ${r1(view.sy(y1))}`);
        }
        for (let y = y0; y <= y1; y += step) {
            g.push(`M${r1(view.sx(x0))} ${r1(view.sy(y))} L${r1(view.sx(x1))} ${r1(view.sy(y))}`);
        }
        o.push(`<path d="${g.join(' ')}" stroke="#26262e" stroke-width="0.7" fill="none"/>`);
    }
    // ── 街道带
    for (const s of inp.streets) {
        const wpx = Math.max(3, s.width * view.scale);
        const x1 = view.sx(s.a[0]);
        const y1 = view.sy(s.a[1]);
        const x2 = view.sx(s.b[0]);
        const y2 = view.sy(s.b[1]);
        o.push(`<line x1="${r1(x1)}" y1="${r1(y1)}" x2="${r1(x2)}" y2="${r1(y2)}" stroke="${STREET_FILL[s.category]}" stroke-width="${r1(wpx)}" stroke-linecap="butt"/>`);
        o.push(`<line x1="${r1(x1)}" y1="${r1(y1)}" x2="${r1(x2)}" y2="${r1(y2)}" stroke="#8d8e98" stroke-width="0.9" stroke-dasharray="5 4" opacity="0.75"/>`);
    }
    // ── 宗地底色：垫在楼位**下面**，一口气看清"这一片是宗地、楼位只占了其中多少"
    for (const z of inp.zoning ?? []) {
        const d = polyPath(z.quad.map((p) => view.pt(p)));
        o.push(`<path d="${d}" fill="#6c5ce7" fill-opacity="0.14" stroke="none"/>`);
    }
    // ── 楼位
    if (inp.showSlots) {
        for (const sl of inp.slots) {
            const d = polyPath(sl.quad.map((p) => view.pt(p)));
            const c = kindColor(sl.kind);
            o.push(`<path d="${d}" fill="${c}" fill-opacity="0.5" stroke="${c}" stroke-width="${sl.moduleWidth ? 0.9 : 1.3}" ${sl.moduleWidth ? '' : 'stroke-dasharray="3 2"'}/>`);
        }
    }
    // ── 宗地描边：画在楼位**之上**。楼位是从宗地切出来的，边界几乎贴合，
    //    虚线如果垫在下面就会被楼位的实边盖掉（第一版就是这么丢的）。
    for (const z of inp.zoning ?? []) {
        const d = polyPath(z.quad.map((p) => view.pt(p)));
        o.push(`<path d="${d}" fill="none" stroke="#b7a6ff" stroke-width="1.8" stroke-dasharray="7 4" stroke-opacity="1"/>`);
    }
    // ── 贴合点：圆点 + 一条指向街的短线（法向）
    if (inp.showPoints) {
        for (const a of inp.attachments) {
            const p = view.pt(a.position);
            const d = a.direction;
            const len = 9;
            if (d) {
                o.push(`<line x1="${r1(p[0])}" y1="${r1(p[1])}" x2="${r1(p[0] + d[0] * len)}" y2="${r1(p[1] - d[1] * len)}" stroke="#5dcaa5" stroke-width="1.2" opacity="0.9"/>`);
            }
            o.push(`<circle cx="${r1(p[0])}" cy="${r1(p[1])}" r="2.6" fill="#5dcaa5"/>`);
        }
    }
    // ── 地块轮廓
    o.push(`<path d="${polyPath(inp.ring.map((p) => view.pt(p)))}" fill="none" stroke="#e8e6e1" stroke-width="1.8"/>`);
    // ── 标记"该临街却没有贴合点"的边
    for (const ei of inp.badEdges) {
        const a = inp.ring[ei];
        const b = inp.ring[(ei + 1) % inp.ring.length];
        if (!a || !b)
            continue;
        o.push(`<line x1="${r1(view.sx(a[0]))}" y1="${r1(view.sy(a[1]))}" x2="${r1(view.sx(b[0]))}" y2="${r1(view.sy(b[1]))}" stroke="#e24b4a" stroke-width="2.6" stroke-linecap="round" opacity="0.9"/>`);
    }
    // ── 句柄（最后画，保证压在最上层）
    // 尺寸刻意小、半透明：22 条街 × 3 个句柄 = 66 个点，画大一点整个画布就看不到几何了。
    // 悬停时由 CSS 放大（.h-st:hover），所以平时小不影响抓取。
    const handles = [];
    for (let i = 0; i < inp.streets.length; i++) {
        const s = inp.streets[i];
        for (const end of [0, 1]) {
            const p = end === 0 ? s.a : s.b;
            handles.push({ kind: 'street-end', streetIdx: i, end, p });
            const sp = view.pt(p);
            o.push(`<circle class="h-st" cx="${r1(sp[0])}" cy="${r1(sp[1])}" r="3.6" fill="#fac775" fill-opacity="0.8" stroke="#15151a" stroke-width="1"/>`);
        }
        const mid = [(s.a[0] + s.b[0]) / 2, (s.a[1] + s.b[1]) / 2];
        handles.push({ kind: 'street-mid', streetIdx: i, p: mid });
        const mp = view.pt(mid);
        o.push(`<rect class="h-st" x="${r1(mp[0] - 3)}" y="${r1(mp[1] - 3)}" width="6" height="6" rx="1.2" fill="#fac775" fill-opacity="0.9" stroke="#15151a" stroke-width="1"/>`);
    }
    for (let i = 0; i < inp.ring.length; i++) {
        const p = inp.ring[i];
        handles.push({ kind: 'ring-vtx', vtx: i, p });
        const sp = view.pt(p);
        o.push(`<circle class="h-rg" cx="${r1(sp[0])}" cy="${r1(sp[1])}" r="4" fill="#efe9dc" fill-opacity="0.9" stroke="#15151a" stroke-width="1"/>`);
    }
    o.push('</svg>');
    return { svg: o.join(''), handles, view, W, H };
}
/** 等轴（伪 3D）体块：复用与静态报告一致的投影，纯字符串。 */
export function drawIso(slots, heights, ring, W = 520) {
    const boxes = slots.map((s, i) => ({ quad: s.quad, h: heights[i] ?? 8, color: kindColor(s.kind) }));
    const base = ring.length ? { quad: ring, h: 0.4, color: '#2b2b34' } : undefined;
    return svgIsoLocal(boxes, W, base);
}
/** 与 demo/render.ts 的 svgIso 同一套投影（30° / 0.5 压缩），这里本地实现以免跨包耦合过深。 */
function svgIsoLocal(boxes, size, base) {
    const all = [];
    for (const b of boxes)
        all.push(...b.quad);
    if (base)
        all.push(...base.quad);
    if (all.length === 0)
        return `<svg viewBox="0 0 ${size} ${size}"></svg>`;
    const bb = boundsOf(all);
    const k = Math.cos(Math.PI / 6);
    const proj = (p, z) => [(p[0] - p[1]) * k, (p[0] + p[1]) * 0.5 - z];
    const flat = [];
    let maxH = 0;
    for (const b of boxes) {
        maxH = Math.max(maxH, b.h);
        flat.push(...b.quad.map((p) => proj(p, 0)), ...b.quad.map((p) => proj(p, b.h)));
    }
    if (base)
        flat.push(...base.quad.map((p) => proj(p, 0)));
    void bb;
    void maxH;
    const fb = boundsOf(flat);
    const fw = Math.max(1, fb.maxX - fb.minX);
    const fh = Math.max(1, fb.maxY - fb.minY);
    const pad = 16;
    const sc = Math.min((size - pad * 2) / fw, (size - pad * 2) / fh);
    const H = Math.max(160, Math.round(fh * sc + pad * 2));
    const tx = (p) => [pad + (p[0] - fb.minX) * sc, pad + (p[1] - fb.minY) * sc];
    // 画家算法：按 (x+y) 从远到近
    const ordered = [...boxes].sort((a, b) => {
        const ca = a.quad.reduce((s, p) => s + p[0] + p[1], 0) / a.quad.length;
        const cb = b.quad.reduce((s, p) => s + p[0] + p[1], 0) / b.quad.length;
        return ca - cb;
    });
    const o = [];
    o.push(`<svg viewBox="0 0 ${size} ${H}" width="100%"><rect x="0" y="0" width="${size}" height="${H}" fill="#15151a" rx="10"/>`);
    if (base) {
        const d = base.quad.map((p, i) => `${i === 0 ? 'M' : 'L'}${tx(proj(p, 0))[0].toFixed(1)} ${tx(proj(p, 0))[1].toFixed(1)}`).join(' ') + ' Z';
        o.push(`<path d="${d}" fill="${base.color}" fill-opacity="0.55" stroke="#3a3a44" stroke-width="1"/>`);
    }
    for (const b of ordered) {
        const top = b.quad.map((p) => tx(proj(p, b.h)));
        const bot = b.quad.map((p) => tx(proj(p, 0)));
        const sides = [];
        for (let i = 0; i < b.quad.length; i++) {
            const j = (i + 1) % b.quad.length;
            sides.push(`M${bot[i][0].toFixed(1)} ${bot[i][1].toFixed(1)} L${bot[j][0].toFixed(1)} ${bot[j][1].toFixed(1)} L${top[j][0].toFixed(1)} ${top[j][1].toFixed(1)} L${top[i][0].toFixed(1)} ${top[i][1].toFixed(1)} Z`);
        }
        o.push(`<path d="${sides.join(' ')}" fill="${b.color}" fill-opacity="0.34" stroke="${b.color}" stroke-width="0.6"/>`);
        o.push(`<path d="${polyPath(top)}" fill="${b.color}" fill-opacity="0.8" stroke="${b.color}" stroke-width="0.8"/>`);
    }
    o.push('</svg>');
    return o.join('');
}
