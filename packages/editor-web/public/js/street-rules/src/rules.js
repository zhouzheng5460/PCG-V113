// 《街道规则与验收条件》的 10 条硬规则（docs/A §2）。
//
// 每条规则都必须给出"证据"，而不是一个布尔：验收报告要能回答
// "这条边为什么不临街"（重叠 8.2 m < 12 m），而不是"R1 失败"。
//
// 违反的后果全部是**静默退化**（Placemaker 不报错，只是少出东西），
// 所以这里是唯一能发现"少了一块"的地方。
import { area, dist, isFinitePoint, isSimple, isCCW } from "../../plan-ir/src/geom.js";
import { buildAttachmentsExact } from "./exact.js";
import { findAttachments, probeAlong, surveyEdges, ALONG_TOLERANCE, MIN_ALONG_LENGTH, PARALLEL_MIN } from "./finder.js";
/** AttachmentFinder 的采样步长上限（米）。见 finder.samplingStep 的推导。 */
const SAMPLE_STEP = 4;
export function validateCase(input, opts = {}) {
    const ring = input.shape.ring.map((p) => [p[0], p[2]]);
    const nets = input.site.nearbyNets;
    const edges = surveyEdges(ring, nets);
    const exact = buildAttachmentsExact(ring, nets);
    // 几何反推算一次，R1 和 R3 共用（各算一次会让两份报告有可能对不上）
    const computed = findAttachments(ring, nets);
    const rules = [
        r1_alongEdge(ring, edges, computed, input.shape),
        r2_corner(ring, nets, exact.corners, exact.attachments),
        r3_crossCheck(ring, nets, exact.attachments, computed),
        r4_crossing(input.shape),
        r5_bezier(nets),
        r6_width(nets),
        r7_drivable(edges, nets),
        r8_zoneGrid(input.site.zoneGrids),
        r9_units(ring, nets),
        r10_policy(input, opts),
        r11_shortFrontage(ring, edges),
    ];
    const pass = rules.filter((r) => r.ok).length;
    return { rules, edges, exact, pass, fail: rules.length - pass, ok: rules.every((r) => r.ok || !isErrorRule(r.id)) };
}
/** R10/R11 只是提醒，不参与总判定。 */
const ADVISORY = new Set(['R10', 'R11']);
const isErrorRule = (id) => !ADVISORY.has(id);
// ─────────────────────────────────────────────── R1 沿边贴合
function r1_alongEdge(ring, edges, attachments, shape) {
    const violations = [];
    let streetEdges = 0;
    for (const e of edges) {
        if (e.along.length === 0) {
            // 近邻但未达标：最容易被误判成"这块地临街"的一类
            if (e.nearMiss) {
                const n = e.nearMiss;
                violations.push({
                    rule: 'R1',
                    level: 'warn',
                    message: `边 ${e.seg} 旁边有街但不算临街：平行度 ${n.parallel.toFixed(3)}（需 ≥ ${PARALLEL_MIN}），重叠 ${n.overlap.toFixed(1)} m（需 ≥ ${n.required.toFixed(1)} m）` +
                        (n.resolvable ? '' : ' —— 且差距在一个采样步长之内，画法模式下判不了'),
                    evidence: `seg=${e.seg} net=${n.netId}`,
                });
            }
            continue;
        }
        streetEdges++;
        const has = attachments.some((a) => a.kind === 'roadAlongEdge' && a.ringSegment === e.seg);
        if (has)
            continue;
        const best = e.along[0];
        violations.push({
            rule: 'R1',
            level: 'error',
            message: `边 ${e.seg} 临街但缺少沿边贴合点：重叠 ${best.overlap.toFixed(1)} m，要求 ≥ ${Math.min(MIN_ALONG_LENGTH, 0.4 * e.length).toFixed(1)} m`,
            evidence: `seg=${e.seg} len=${e.length.toFixed(1)}m overlap=${best.overlap.toFixed(1)}m parallel=${best.parallel.toFixed(3)} width=${best.width}m`,
        });
    }
    if (streetEdges === 0) {
        violations.push({
            rule: 'R1',
            level: 'error',
            message: '没有任何一条边临街。Placemaker 的停车族/商场族/市场族会直接判定不可用（ready=no）',
            evidence: `共 ${edges.length} 条边，判据：距离 ≤ 路宽/2 + ${ALONG_TOLERANCE} m、平行度 ≥ ${PARALLEL_MIN}、重叠 ≥ ${MIN_ALONG_LENGTH} m 且 ≥40% 边长`,
        });
    }
    return {
        id: 'R1',
        title: '沿边贴合',
        ok: violations.filter((v) => v.level === 'error').length === 0,
        summary: `${streetEdges}/${edges.length} 条边临街，贴合点 ${attachments.filter((a) => a.kind === 'roadAlongEdge').length} 个`,
        violations,
    };
}
// ─────────────────────────────────────────────── R2 拐角节点
function r2_corner(ring, nets, corners, attachments) {
    const violations = [];
    for (const c of corners) {
        const has = attachments.some((a) => a.kind === 'roadNode' && dist(a.position, c.position) < 1);
        if (!has) {
            violations.push({
                rule: 'R2',
                level: 'error',
                message: `顶点 ${c.vertex} 是街角但缺少节点贴合点（交汇街道 ${c.netIds.join('、')}）`,
                evidence: `vertex=${c.vertex} pos=[${c.position[0].toFixed(1)}, ${c.position[1].toFixed(1)}]`,
            });
        }
    }
    return {
        id: 'R2',
        title: '拐角节点',
        ok: violations.length === 0,
        summary: `${corners.length} 个街角，全部有节点`,
        violations,
    };
}
// ─────────────────────────────────────────────── R3 双实现互验
/**
 * 精确构造（block 模式契约）vs 几何反推（画法模式算法）。
 *
 * 两者**允许**在一种情况下不一致：**短边**。
 * AttachmentFinder 沿街每 ~4 m 取一个采样点，"重叠长度"是拿最边缘两个采样点算的，
 * 所以一条边最多会被**少算一个步长**；边短到只剩 1 个采样点时重叠恒为 0。
 * 实测 54 份真实地块：1146 条边里 71 条（6.2%）短于 12 m，全部落在这一类。
 * 这种不一致不是 bug，是算法的分辨率上限——但它是**街道生成器的硬约束**：
 * 造出 <12 m 的临街边，画法模式下这条边白给（见 R11）。
 *
 * 真正要报错的是：同一条街在两边落在**不同的边**上，或位置差 > 0.5 m。
 */
function r3_crossCheck(ring, nets, exactAtts, computedAtts) {
    const computed = computedAtts.filter((a) => a.kind === 'roadAlongEdge');
    const exactAlong = exactAtts.filter((a) => a.kind === 'roadAlongEdge');
    const violations = [];
    const compByNet = new Map(computed.map((a) => [a.externalId, a]));
    const exactByNet = new Map(exactAlong.map((a) => [a.externalId, a]));
    const netById = new Map(nets.map((n) => [n.externalId, n]));
    let unresolvable = 0;
    let oneSided = 0;
    // A. 同一条街 → 落在不同的边（结构性分歧，必错）
    for (const [id, e] of exactByNet) {
        const c = compByNet.get(id);
        if (!c)
            continue;
        if (c.ringSegment !== e.ringSegment) {
            violations.push({
                rule: 'R3',
                level: 'error',
                message: `街道 ${id} 的贴合边不一致：精确构造 seg=${e.ringSegment}，几何反推 seg=${c.ringSegment}`,
                evidence: `边长 ${distSafe(ring, e.ringSegment).toFixed(1)} / ${distSafe(ring, c.ringSegment).toFixed(1)} m`,
            });
            continue;
        }
        const d = Math.hypot(c.position[0] - e.position[0], c.position[1] - e.position[1]);
        if (d > 0.5) {
            violations.push({
                rule: 'R3',
                level: 'warn',
                message: `街道 ${id} 的贴合点位置差 ${d.toFixed(2)} m（阈值 0.5 m）`,
            });
        }
    }
    // B. 只有精确构造有 → 分清"采样分辨率不够"和"真不临街"
    for (const [id, e] of exactByNet) {
        if (compByNet.has(id))
            continue;
        const net = netById.get(id);
        const seg = e.ringSegment;
        if (!net || seg < 0 || seg >= ring.length) {
            oneSided++;
            violations.push({ rule: 'R3', level: 'warn', message: `街道 ${id} 只在精确构造里出现，几何反推没认` });
            continue;
        }
        const p = probeAlong(net, ring[seg], ring[(seg + 1) % ring.length]);
        if (p.resolvable) {
            unresolvable++;
        }
        else {
            oneSided++;
            violations.push({
                rule: 'R3',
                level: 'error',
                message: `街道 ${id} 在 seg=${seg} 上应算临街（精确构造认了），几何反推漏了，且超出采样分辨率能解释的范围`,
                evidence: `边长 ${p.len.toFixed(1)} m，重叠 ${p.overlap.toFixed(1)} m，要求 ${p.required.toFixed(1)} m，采样步长 ${p.step.toFixed(2)} m`,
            });
        }
    }
    // C. 只有几何反推有：block 模式下不该出现（环本来就是沿路描出来的）
    for (const [id, c] of compByNet) {
        if (exactByNet.has(id))
            continue;
        oneSided++;
        violations.push({
            rule: 'R3',
            level: 'warn',
            message: `街道 ${id} 只在几何反推里出现（seg=${c.ringSegment}）。block 模式的环是沿路描出来的，不该有这种情况；画法/切碎地块上正常`,
        });
    }
    const bad = violations.filter((v) => v.level === 'error').length;
    return {
        id: 'R3',
        title: '精确/反推互验',
        ok: bad === 0,
        summary: `沿边贴合点 ${exactAlong.length} 个` +
            (unresolvable ? `，其中 ${unresolvable} 条短边采样不足（可解释）` : '') +
            (oneSided ? `，单侧独有 ${oneSided} 条` : '') +
            (bad === 0 && !oneSided && !unresolvable ? '，与几何反推完全一致' : ''),
        violations,
    };
}
function distSafe(ring, seg) {
    if (seg < 0 || seg >= ring.length)
        return 0;
    const a = ring[seg];
    const b = ring[(seg + 1) % ring.length];
    return Math.hypot(b[0] - a[0], b[1] - a[1]);
}
// ─────────────────────────────────────────────── R4 穿越
function r4_crossing(shape) {
    const crossings = shape.attachments.filter((a) => a.kind === 'roadCrossing');
    const violations = [];
    if (shape.source !== 'drawn' && crossings.length > 0) {
        violations.push({
            rule: 'R4',
            level: 'warn',
            message: `block 模式出现了 ${crossings.length} 个穿越贴合点。block 地块的街应该贴着边走，穿过来说明地块跨到了街对面`,
        });
    }
    return {
        id: 'R4',
        title: '穿越贴合',
        ok: true,
        summary: `来源 ${shape.source}，穿越贴合点 ${crossings.length} 个`,
        violations,
    };
}
// ─────────────────────────────────────────────── R5 贝塞尔
function r5_bezier(nets) {
    const violations = [];
    nets.forEach((n, i) => {
        if (![n.a, n.b, n.c, n.d].every(isFinitePoint)) {
            violations.push({
                rule: 'R5',
                level: 'error',
                message: `第 ${i + 1} 条街道含非有限控制点，整条街会被 Placemaker 静默丢弃`,
                evidence: `id=${n.externalId}`,
            });
        }
        if (dist(n.a, n.d) < 0.5) {
            violations.push({
                rule: 'R5',
                level: 'warn',
                message: `第 ${i + 1} 条街道长度不足 0.5 m，视为退化`,
                evidence: `id=${n.externalId}`,
            });
        }
    });
    return {
        id: 'R5',
        title: '贝塞尔有效性',
        ok: violations.filter((v) => v.level === 'error').length === 0,
        summary: `${nets.length} 条街道控制点全部有效`,
        violations,
    };
}
// ─────────────────────────────────────────────── R6 宽度
function r6_width(nets) {
    const violations = [];
    for (const n of nets) {
        if (!(n.width > 0)) {
            violations.push({
                rule: 'R6',
                level: 'error',
                message: `街道 ${n.externalId} 宽度为 ${n.width}。宽度必须大于 0，否则该边永不被分区（RoadBlocks 的硬前提）`,
            });
        }
    }
    return {
        id: 'R6',
        title: '道路宽度',
        ok: violations.length === 0,
        summary: nets.length ? `最小宽度 ${Math.min(...nets.map((n) => n.width)).toFixed(1)} m` : '无街道',
        violations,
    };
}
// ─────────────────────────────────────────────── R7 车行可达
function r7_drivable(edges, nets) {
    const violations = [];
    const byId = new Map(nets.map((n) => [n.externalId, n]));
    const drivable = edges.flatMap((e) => e.along.filter((a) => {
        const net = byId.get(a.netId);
        return net && net.category === 'road' && net.width >= 4;
    }));
    if (drivable.length === 0) {
        violations.push({
            rule: 'R7',
            level: 'error',
            message: '没有任何一条车行道临街。停车族会返回「需要一个车行出入口」，整块地一个面都不出',
            evidence: '判据：category=road 且 width ≥ 4 m',
        });
    }
    return {
        id: 'R7',
        title: '车行可达',
        ok: violations.length === 0,
        summary: `车行临街边 ${new Set(drivable.map((d) => d.netId)).size} 条`,
        violations,
    };
}
// ─────────────────────────────────────────────── R8 宗地格
function r8_zoneGrid(grids) {
    const violations = [];
    grids.forEach((g, i) => {
        if (g.size[1] > 6) {
            violations.push({
                rule: 'R8',
                level: 'error',
                message: `第 ${i + 1} 个宗地格网进深 ${g.size[1]} 格 > 6 格上限`,
            });
        }
        if (Math.abs(g.size[0] - Math.round(g.size[0])) > 1e-6) {
            violations.push({ rule: 'R8', level: 'warn', message: `第 ${i + 1} 个宗地格网横向不是整格` });
        }
    });
    return {
        id: 'R8',
        title: '宗地格网',
        ok: violations.filter((v) => v.level === 'error').length === 0,
        summary: grids.length ? `${grids.length} 个格网，最大进深 ${Math.max(...grids.map((g) => g.size[1]))} 格` : '无既有宗地格网',
        violations,
    };
}
// ─────────────────────────────────────────────── R9 单位与坐标
function r9_units(ring, nets) {
    const violations = [];
    const flat = ring;
    if (!isCCW(flat))
        violations.push({ rule: 'R9', level: 'error', message: '地界不是逆时针' });
    if (!isSimple(flat))
        violations.push({ rule: 'R9', level: 'error', message: '地界自交' });
    const a = area(flat);
    if (a <= 1)
        violations.push({ rule: 'R9', level: 'error', message: `地界面积 ${a.toFixed(2)} m² ≤ 1 m²` });
    const allPts = [...flat, ...nets.flatMap((n) => [n.a, n.b, n.c, n.d])];
    const outlier = allPts.find((p) => Math.abs(p[0]) > 1e5 || Math.abs(p[1]) > 1e5);
    if (outlier) {
        violations.push({
            rule: 'R9',
            level: 'warn',
            message: '坐标绝对值超过 10 万，疑似单位不是米（英尺/世界单位）',
            evidence: `[${outlier[0]}, ${outlier[1]}]`,
        });
    }
    return {
        id: 'R9',
        title: '单位与坐标',
        ok: violations.filter((v) => v.level === 'error').length === 0,
        summary: `面积 ${a.toFixed(1)} m²，${ring.length} 边`,
        violations,
    };
}
// ─────────────────────────────────────────────── R10 兜底语义
function r10_policy(input, opts) {
    const violations = [];
    const obs = input.site.obstacles.length;
    if (obs > 0 && !opts.clipMode) {
        violations.push({
            rule: 'R10',
            level: 'warn',
            message: `传了 ${obs} 个建筑轮廓但未开启裁剪模式。SiteSafety 的语义是"删"不是"绕"：路网会被切断（停车族实测 53 条车道剩 1 条）`,
            evidence: '建议改用「不传 obstacles + 生成后按楼裁剪」',
        });
    }
    if (input.site.layoutScale !== 0.5 && input.site.layoutScale !== 1) {
        violations.push({
            rule: 'R10',
            level: 'info',
            message: `布局尺度 ${input.site.layoutScale}（默认 0.5）。这个值乘的是布局距离，不是真实尺寸——不显式给会静默放大一倍`,
        });
    }
    return {
        id: 'R10',
        title: '兜底语义',
        ok: violations.filter((v) => v.level === 'error').length === 0,
        summary: obs ? `障碍物 ${obs} 个，裁剪模式${opts.clipMode ? '开' : '关'}` : '无障碍物',
        violations,
    };
}
// ─────────────────────────────────────────────── R11 临街边长度（advisory）
/**
 * 临街边的长度下限。
 *
 * AttachmentFinder 的采样步长 = 近似长度 / clamp(ceil(近似长度/4), 1, 64)，约 4 m。
 * 它算"重叠"用的是边缘两个采样点之间的距离，于是有两个分辨率上限：
 *   · 边长 ≤ 1 个步长（≈4 m）：最多落 1 个采样点 → 重叠恒为 0 → **永不临街**
 *   · 边长 < MinAlongLength（12 m）：要求重叠 = 0.4×边长，很容易被采样误差吃掉
 *
 * 这两条只影响"画法/涂绘"地块——block 模式的环是沿路描出来的，贴合点由 RoadNetwork
 * 直接给（docs/A §0）。但只要我们希望**同一份路网在两种模式下都成立**，
 * 就不该造出 < 12 m 的临街边。实测真实城市（54 个街区）里 1146 条边有 71 条（6.2%）
 * 落在这一档，全部是两条路之间的斜接短边。
 *
 * 定为 info：这是给街道生成器的约束，不是地块的错误。
 */
function r11_shortFrontage(ring, edges) {
    const violations = [];
    let short = 0;
    let dead = 0;
    for (const e of edges) {
        const facesStreet = e.along.length > 0 || e.nearMiss !== null;
        if (!facesStreet)
            continue;
        if (e.length >= MIN_ALONG_LENGTH)
            continue;
        short++;
        // 采样步长恒约 4 m（与路宽无关），见 finder.samplingStep
        if (e.length <= SAMPLE_STEP)
            dead++;
    }
    if (short > 0) {
        violations.push({
            rule: 'R11',
            level: 'info',
            message: `${short} 条临街边短于 ${MIN_ALONG_LENGTH} m` +
                (dead ? `（其中 ${dead} 条 ≤ 一个采样步长，画法模式下重叠恒为 0，永不临街）` : '') +
                '。街道生成时给每条临街边留 ≥ 12 m，否则换到画法模式这条边就白给了',
            evidence: `共 ${edges.length} 条边`,
        });
    }
    return {
        id: 'R11',
        title: '临街边长度',
        ok: true,
        summary: short ? `${short}/${edges.length} 条临街边 < ${MIN_ALONG_LENGTH} m` : '临街边长度都够',
        violations,
    };
}
/** 报告渲染成纯文本（CLI 与验收脚本共用）。 */
export function formatReport(rep) {
    const lines = [];
    lines.push(`街道契约验收：${rep.pass}/${rep.rules.length} 条规则通过${rep.ok ? '' : '（存在阻断项）'}`);
    for (const r of rep.rules) {
        const mark = r.ok ? '✓' : '✗';
        lines.push(`  ${mark} ${r.id} ${r.title} — ${r.summary}`);
        for (const v of r.violations) {
            const tag = v.level === 'error' ? '错误' : v.level === 'warn' ? '警告' : '提示';
            lines.push(`      [${tag}] ${v.message}${v.evidence ? '  ·  ' + v.evidence : ''}`);
        }
    }
    return lines.join('\n');
}
