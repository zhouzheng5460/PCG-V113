// 与真 C# 的对照：把 Core/Tool/AttachmentFinder 的输出当基准，逐点比对我们的移植。
//
// 这是 docs/A R3 的第二种互验（第一种是"精确构造 vs 几何反推"，两边都是我们写的）。
// 只有这一层才能回答"移植得像不像"——否则两个实现一起错，互相验证永远通过。
//
// 基准由 tools/oracle/driver/Attachments.cs 产出（--attachments 模式）。
import { dist, dot, norm } from "../../plan-ir/src/geom.js";
import { netPointAt } from "./finder.js";
const keyOf = (a) => `${a.kind}#${a.externalId}@${a.ringSegment}`;
/**
 * 逐点比对。容差默认 0.01 m（1 cm）：
 * C# 用 float32 算贝塞尔与距离，我们走 float64，1 cm 内的差异属于精度而非逻辑。
 *
 * 传了 `nets` 之后会额外识别 **externalT 的平局**：
 * 沿边贴合点的 externalT 取的是"离边中点最近的采样点"，当真中点正好落在两个采样点
 * 的垂直平分线上时，两边是**完全等距**的。C# 用 float32 算 `distancesq`，噪声决定
 * 它挑前一个还是后一个；我们定向挑前一个（见 finder.ts 的 TIE_EPS）。
 * 实测 54 份真实地块 673 个贴合点里有 3 个（0.45%）落在这种精确平局上——
 * 位置完全一致，只差 externalT 一个采样格。这种差异记成 tie，不算不一致。
 */
export function compareAttachments(ts, cs, tol = 0.01, nets) {
    const rows = [];
    const onlyTs = [];
    const onlyCs = [];
    let ties = 0;
    const netById = new Map((nets ?? []).map((n) => [n.externalId, n]));
    /** 两个 t 到同一个位置的距离是否相等（<1e-3 m）——即"同一个点、平局"。 */
    const tiedT = (net, tTs, tCs, at) => {
        if (!net)
            return false;
        const dTs = dist(netPointAt(net, tTs), at);
        const dCs = dist(netPointAt(net, tCs), at);
        return Math.abs(dTs - dCs) <= 1e-3;
    };
    const tsMap = new Map(ts.map((a) => [keyOf(a), a]));
    const csMap = new Map(cs.attachments.map((a) => [keyOf(a), a]));
    for (const [k, a] of tsMap) {
        const b = csMap.get(k);
        if (!b) {
            onlyTs.push(k);
            continue;
        }
        const dPos = dist([a.position[0], a.position[1]], b.position);
        rows.push({ key: k, field: 'position', ts: `${a.position[0].toFixed(2)},${a.position[1].toFixed(2)}`, cs: `${b.position[0].toFixed(2)},${b.position[1].toFixed(2)}`, pass: dPos <= tol });
        rows.push({ key: k, field: 'ringT', ts: round4(a.ringT), cs: round4(b.ringT), pass: Math.abs(a.ringT - b.ringT) <= 1e-3 });
        rows.push({ key: k, field: 'roadWidth', ts: a.roadWidth, cs: b.roadWidth, pass: Math.abs(a.roadWidth - b.roadWidth) <= tol });
        const tOk = Math.abs(a.externalT - b.externalT) <= 1e-3;
        const tie = !tOk && tiedT(netById.get(a.externalId), a.externalT, b.externalT, a.position);
        if (tie)
            ties++;
        rows.push({ key: k, field: 'externalT', ts: round4(a.externalT), cs: round4(b.externalT), pass: tOk || tie, tie });
        if (a.direction && b.kind !== 'roadNode') {
            const cos = dot(norm(a.direction), norm(b.direction));
            rows.push({ key: k, field: 'direction·', ts: round4(cos), cs: 1, pass: Math.abs(cos - 1) <= 1e-3 });
        }
    }
    for (const k of csMap.keys())
        if (!tsMap.has(k))
            onlyCs.push(k);
    const pass = rows.filter((r) => r.pass).length;
    return { rows, pass, fail: rows.length - pass, ties, onlyTs, onlyCs };
}
const round4 = (n) => Math.round(n * 1e4) / 1e4;
export function formatCompare(name, r) {
    const lines = [];
    const okAll = r.fail === 0 && r.onlyTs.length === 0 && r.onlyCs.length === 0;
    lines.push(`${okAll ? '✓' : '✗'} ${name}：${r.pass}/${r.rows.length} 项一致` +
        (r.ties ? `（含 ${r.ties} 处平局等价）` : '') +
        (r.onlyTs.length || r.onlyCs.length ? `｜仅 TS ${r.onlyTs.length} 个，仅 C# ${r.onlyCs.length} 个` : ''));
    for (const row of r.rows.filter((x) => !x.pass)) {
        lines.push(`    ✗ ${row.key} ${row.field}：TS ${row.ts} vs C# ${row.cs}`);
    }
    for (const k of r.onlyTs)
        lines.push(`    ✗ ${k} 只在 TS 出现`);
    for (const k of r.onlyCs)
        lines.push(`    ✗ ${k} 只在 C# 出现`);
    return lines.join('\n');
}
