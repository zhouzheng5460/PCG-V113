// 两套实现的比对。**这是整个迁移的安全网**：没有它，"原生实现等价于 Placemaker"就是一句话。
//
// 判据分五档而不是通过/失败：
//
//   exact（≤2%）· close（≤20%）· off（≤50%）· bad（>50%）· missing（golden 有、原生完全没有）
//
// 为什么分级：原生实现不可能第一版就逐条对齐，如果只给"失败"，就不知道离成功有多远，
// 于是会退化成"改到不红为止"——这一路已经这样翻过车（D-029 的 `undefined > 0` 假失败、
// D-047 的树密度方向反了却报 PASS）。分级之后，每一步都能看到"哪一项从 bad 变成 off"。
//
// `missing` 单独一档的理由：**沉默的通道缺失是最危险的差异**。
// 原生实现少写一个 `paint` 通道，汇总数只差一点，但下游整层效果消失。

import { measure, roleDiff, rows, type PlanMetrics } from './metrics.ts';
import type { Plan } from './types.ts';

export type Verdict = 'exact' | 'close' | 'off' | 'bad' | 'missing';

export interface MetricComparison {
  key: string;
  label: string;
  group: string;
  golden: number;
  native: number;
  /** 相对 golden 的偏差（带符号）；golden 为 0 时为 NaN */
  rel: number;
  verdict: Verdict;
}

export interface ChannelPresence {
  channel: string;
  golden: number;
  native: number;
  /** golden 有条目而原生是空的 —— 静默缺失 */
  silentLoss: boolean;
}

export interface ComparisonReport {
  generator: string;
  siteArea: number;
  golden: PlanMetrics;
  native: PlanMetrics;
  metrics: MetricComparison[];
  /** 按 role 的明细差（每个通道各一张） */
  roleDiffs: { channel: string; rows: { role: string; golden: number; native: number }[] }[];
  presence: ChannelPresence[];
  /** 一句话总评 */
  verdict: Verdict;
  score: number;
}

const REL_EXACT = 0.02;
const REL_CLOSE = 0.2;
const REL_OFF = 0.5;

/** 绝对容差：小于它的差异当 0 看（避免 0.0001 vs 0.0002 这种噪声被判成 bad） */
const ABS_FLOOR: Record<string, number> = {
  surfaceTotal: 5,
  netTotal: 5,
  objectTotal: 2,
  parkingBays: 2,
  accessPoints: 0,
  overlapArea: 20,
  paintArea: 20,
};

function verdictOf(golden: number, native: number, key: string): { rel: number; verdict: Verdict } {
  const floor = ABS_FLOOR[key] ?? 0;
  if (Math.abs(golden - native) <= floor) return { rel: 0, verdict: 'exact' };
  if (golden === 0) return { rel: NaN, verdict: native === 0 ? 'exact' : 'bad' };
  const rel = (native - golden) / golden;
  const a = Math.abs(rel);
  if (a <= REL_EXACT) return { rel, verdict: 'exact' };
  if (a <= REL_CLOSE) return { rel, verdict: 'close' };
  if (a <= REL_OFF) return { rel, verdict: 'off' };
  return { rel, verdict: 'bad' };
}

const RANK: Record<Verdict, number> = { exact: 0, close: 1, off: 2, bad: 3, missing: 4 };

export function compare(gen: string, golden: Plan, native: Plan): ComparisonReport {
  const gm = measure(golden);
  const nm = measure(native);

  const metrics: MetricComparison[] = rows(gm).map((r) => {
    const nv = rows(nm).find((x) => x.key === r.key)?.value ?? 0;
    const { rel, verdict } = verdictOf(r.value, nv, r.key);
    return { key: r.key, label: r.label, group: r.group, golden: r.value, native: nv, rel, verdict };
  });

  const channels: [string, Record<string, number>][] = [
    ['surfaces（按 role 面积）', gm.surfaceByRole],
    ['nets（按 role 长度）', gm.netByRole],
    ['objects（按 role 计数）', gm.objectByRole],
  ];
  const roleDiffs = channels.map(([channel, g]) => {
    const n = channel.startsWith('surfaces') ? nm.surfaceByRole : channel.startsWith('nets') ? nm.netByRole : nm.objectByRole;
    return { channel, rows: roleDiff(g, n) };
  });

  const presence: ChannelPresence[] = [
    { channel: 'surfaces', golden: golden.surfaces.length, native: native.surfaces.length },
    { channel: 'nets', golden: golden.nets.length, native: native.nets.length },
    { channel: 'objects', golden: golden.objects.length, native: native.objects.length },
    { channel: 'zoning', golden: golden.zoning.length, native: native.zoning.length },
    { channel: 'paint', golden: golden.paint.length, native: native.paint.length },
    { channel: 'terraform', golden: golden.terraform.length, native: native.terraform.length },
    { channel: 'issues', golden: golden.issues.length, native: native.issues.length },
  ].map((p) => ({ ...p, silentLoss: p.golden > 0 && p.native === 0 }));

  // 总评：取最差一项；但 `issues` 通道缺失不算（它本来就是诊断信息，不是产物）
  let worst: Verdict = 'exact';
  for (const m of metrics) {
    if (m.key === 'issues' || m.group === '车道效率') continue;
    if (RANK[m.verdict] > RANK[worst]) worst = m.verdict;
  }
  for (const p of presence) {
    if (p.channel === 'issues') continue;
    if (p.silentLoss && RANK['missing'] > RANK[worst]) worst = 'missing';
  }

  // 分数：0~100，每项按档给分再平均。只看"离对齐还有多远"，不是质量分。
  //
  // **诊断量不计分**（用户 2026-11-03："先增加一个诊断量 … 然后直接看 p1 的 deadAisleRatio"）。
  // `车道效率` 组的两个量是**用来找病灶的仪器**，不是保真目标：
  // golden 自己也造 dead aisle（18%），拿它当分数会把"效率诊断"变成"复制 golden 的空转"。
  // 它们照常打印、照常参与 roleDiff，只是不进分母。
  const scoreOf = (v: Verdict): number => ({ exact: 1, close: 0.7, off: 0.3, bad: 0, missing: 0 })[v];
  const graded = metrics.filter((m) => m.key !== 'issues' && m.group !== '车道效率');
  const score = Math.round((graded.reduce((s, m) => s + scoreOf(m.verdict), 0) / Math.max(1, graded.length)) * 100);

  return { generator: gen, siteArea: golden.area, golden: gm, native: nm, metrics, roleDiffs, presence, verdict: worst, score };
}

/** 给人看的表格文本 */
export function formatReport(r: ComparisonReport): string {
  const fmt = (v: number): string => (Number.isInteger(v) ? String(v) : v.toFixed(Math.abs(v) < 10 ? 2 : 0));
  const lines: string[] = [];
  lines.push(`── ${r.generator} · 地块 ${fmt(r.siteArea)} m² ── golden vs native（分数 ${r.score}/100 · 总评 ${r.verdict}）`);
  let group = '';
  for (const m of r.metrics) {
    if (m.group !== group) {
      group = m.group;
      lines.push(`  [${group}]`);
    }
    const rel = Number.isNaN(m.rel) ? '  —  ' : `${m.rel >= 0 ? '+' : ''}${(m.rel * 100).toFixed(0)}%`;
    lines.push(`    ${m.label.padEnd(18)} ${fmt(m.golden).padStart(8)} → ${fmt(m.native).padStart(8)}  ${rel.padStart(5)}  ${m.verdict}`);
  }
  const lost = r.presence.filter((p) => p.silentLoss);
  if (lost.length) lines.push(`  ! 静默缺失通道：${lost.map((p) => p.channel).join(', ')}`);
  for (const d of r.roleDiffs) {
    if (!d.rows.length) continue;
    lines.push(`  [${d.channel} 明细差]`);
    for (const row of d.rows.slice(0, 8)) {
      lines.push(`    ${row.role.padEnd(18)} ${fmt(row.golden).padStart(8)} vs ${fmt(row.native).padStart(8)}`);
    }
  }
  return lines.join('\n');
}
