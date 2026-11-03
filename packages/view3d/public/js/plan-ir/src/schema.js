// 运行期校验：进 Plan IR 之前先过闸。
//
// 为什么必须有：Placemaker 对坏输入的容忍方式是**静默丢弃**（非有限贝塞尔直接跳过、
// 面积 <0.5 m² 的障碍物直接无视、宽度 0 的边永不分区）。所以"数据错了"在它那边
// 表现为"少了一块"，不会报错。这道闸负责把"少了一块"变成"第 3 条街 b 点 NaN"。
import { area, isFinitePoint, isFiniteRing, isSimple, isCCW, signedArea } from "./geom.js";
const ok = (code, message, detail) => ({ code, ok: true, message, detail });
const bad = (code, message, detail) => ({ code, ok: false, message, detail });
/** 输入契约校验（对应 docs/A 的 R5/R6/R9 与 B 的 quad 规则）。 */
export function validateCase(input) {
    const out = [];
    const { ring } = input.shape;
    if (ring.length < 3)
        out.push(bad('ring.min', '地界至少 3 个点', `实际 ${ring.length} 个`));
    const flat = ring.map((p) => [p[0], p[2]]);
    if (!isFiniteRing(flat))
        out.push(bad('ring.finite', '地界含非有限坐标'));
    if (flat.length >= 3) {
        if (!isCCW(flat))
            out.push(bad('ring.ccw', `地界必须是逆时针（当前面积 ${signedArea(flat).toFixed(2)} m²，为负）`));
        if (!isSimple(flat))
            out.push(bad('ring.simple', '地界自交'));
        const a = area(flat);
        if (a <= 1)
            out.push(bad('ring.area', '地界面积必须大于 1 m²', `实际 ${a.toFixed(3)} m²`));
        else
            out.push(ok('ring.area', `地界 ${a.toFixed(1)} m²`));
    }
    input.site.nearbyNets.forEach((n, i) => {
        const pts = [n.a, n.b, n.c, n.d];
        if (!pts.every(isFinitePoint)) {
            out.push(bad('net.finite', `第 ${i + 1} 条街道含非有限控制点`, `id=${n.externalId}`));
        }
        if (!(n.width > 0)) {
            out.push(bad('net.width', `第 ${i + 1} 条街道宽度必须大于 0`, `id=${n.externalId} width=${n.width}`));
        }
    });
    input.site.obstacles.forEach((o, i) => {
        const a = area(o.quad);
        if (a < 0.5)
            out.push(bad('obstacle.area', `第 ${i + 1} 个避让对象面积小于 0.5 m²，会被静默无视`, `${a.toFixed(2)} m²`));
    });
    input.site.zoneGrids.forEach((g, i) => {
        if (g.size[1] > 6)
            out.push(bad('zonegrid.depth', `第 ${i + 1} 个宗地格网进深 ${g.size[1]} 格，超过上限 6 格`));
    });
    if (!(input.site.layoutScale >= 0.25 && input.site.layoutScale <= 1.5)) {
        out.push(bad('site.layoutScale', '布局尺度必须在 0.25~1.5 之间', `实际 ${input.site.layoutScale}`));
    }
    if (!(input.site.edgeMargin >= 0))
        out.push(bad('site.edgeMargin', '退让不能为负'));
    const n = ring.length;
    input.shape.attachments.forEach((a, i) => {
        if (a.ringSegment < 0 || a.ringSegment >= n) {
            out.push(bad('attach.seg', `第 ${i + 1} 个贴合点的 ringSegment 越界`, `${a.ringSegment} ∉ [0,${n})`));
        }
        if (!(a.ringT >= 0 && a.ringT <= 1))
            out.push(bad('attach.t', `第 ${i + 1} 个贴合点的 ringT 越界`, String(a.ringT)));
        if (a.kind === 'roadAlongEdge' && !(a.roadWidth > 0)) {
            out.push(bad('attach.width', `第 ${i + 1} 个沿边贴合点 roadWidth 必须大于 0（否则该边永不被分区）`));
        }
    });
    return out;
}
/** 输出 Plan 结构校验。 */
export function validatePlan(plan) {
    const out = [];
    if (plan.protocol !== 'placemaker-plan/1')
        out.push(bad('plan.protocol', '协议版本不是 placemaker-plan/1', plan.protocol));
    if (plan.units !== 'meters')
        out.push(bad('plan.units', '单位必须是米', plan.units));
    if (typeof plan.ready !== 'boolean')
        out.push(bad('plan.ready', 'ready 必须是布尔'));
    plan.surfaces.forEach((s, i) => {
        if (s.ring.length < 3)
            out.push(bad('surface.ring', `第 ${i + 1} 个面少于 3 点`));
        else if (!isFiniteRing(s.ring))
            out.push(bad('surface.finite', `第 ${i + 1} 个面含非有限坐标`));
    });
    plan.nets.forEach((n, i) => {
        if (![n.a, n.b, n.c, n.d].every(isFinitePoint))
            out.push(bad('plan.net', `第 ${i + 1} 条渠含非有限控制点`));
    });
    plan.objects.forEach((o, i) => {
        if (!Number.isFinite(o.x) || !Number.isFinite(o.z))
            out.push(bad('plan.object', `第 ${i + 1} 个物件位置非有限`));
    });
    plan.zoning.forEach((z, i) => {
        if (z.quad.length !== 4)
            out.push(bad('plan.zoning', `第 ${i + 1} 块宗地不是四边形`));
        else if (area(z.quad) < 1)
            out.push(bad('plan.zoning.area', `第 ${i + 1} 块宗地面积过小`, `${area(z.quad).toFixed(2)} m²`));
    });
    if (out.length === 0)
        out.push(ok('plan.ok', '方案结构通过'));
    return out;
}
export function summarize(checks) {
    let pass = 0;
    let fail = 0;
    for (const c of checks)
        (c.ok ? pass++ : fail++);
    return { pass, fail };
}
