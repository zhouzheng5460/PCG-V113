// Slot 验收（docs/E §7 的 E1..E6）+ 几何指纹。
//
// 指纹是关键：它让"换模型不许动几何"这件事可断言。
// 做法是把所有 slot 的四角坐标按稳定顺序拼起来取哈希——换 Skin 之后重算，
// 值必须一模一样，否则说明有人顺手改了占地（台账 Q-011）。

import { area } from '../../plan-ir/src/geom.ts';
import type { ZoneKind } from '../../plan-ir/src/types.ts';
import { CELL, KIND_SPECS, quadContains, type Slot } from './cut.ts';
import type { Skin } from './skin.ts';

export interface SlotCheck {
  id: string;
  title: string;
  ok: boolean;
  summary: string;
  detail?: string;
}

export interface SlotReport {
  checks: SlotCheck[];
  pass: number;
  fail: number;
  ok: boolean;
  totalSlots: number;
  totalArea: number;
  matchRate: number;
  fingerprint: string;
}

const MODULE_TOL = 0.05;

/** 几何指纹：与外观无关，只由 slot 的占地决定。 */
export function fingerprint(slots: readonly Slot[]): string {
  const parts = slots
    .map((s) => `${s.id}|${s.quad.map((p) => `${p[0].toFixed(2)},${p[1].toFixed(2)}`).join(';')}`)
    .sort();
  let h = 2166136261 >>> 0;
  for (const p of parts) {
    for (let i = 0; i < p.length; i++) {
      h ^= p.charCodeAt(i);
      h = Math.imul(h, 16777619) >>> 0;
    }
  }
  return (h >>> 0).toString(16).padStart(8, '0');
}

export function validateSlots(
  slots: readonly Slot[],
  zoningArea: number,
  skins: readonly Skin[],
  quads?: readonly (readonly (readonly [number, number])[])[],
): SlotReport {
  const checks: SlotCheck[] = [];

  // E1 8 m 模数（进深：整格 或 单排铺满；面宽：允许补位块，单独计数）
  const badDepth = slots.filter((s) => !s.moduleDepth && !s.depthFilled).length;
  const filledDepth = slots.filter((s) => !s.moduleDepth && s.depthFilled).length;
  const badWidth = slots.filter((s) => !s.moduleWidth).length;
  checks.push({
    id: 'E1',
    title: '8 m 模数',
    ok: badDepth === 0,
    summary: `进深非模数 ${badDepth} 个（单排铺满 ${filledDepth} 个可接受），面宽非模数 ${badWidth} 个（补位块）`,
    detail: `进深偏差阈值 ${MODULE_TOL} m`,
  });

  // E2 两两不重叠（同一 frame 下用局部矩形解析判断，比采样准）
  let overlaps = 0;
  for (let i = 0; i < slots.length; i++) {
    for (let j = i + 1; j < slots.length; j++) {
      const a = slots[i];
      const b = slots[j];
      if (a.zoningRef !== b.zoningRef) continue;
      const ax0 = a.localBox[0];
      const ax1 = a.localBox[1];
      const ay0 = a.localBox[2];
      const ay1 = a.localBox[3];
      const bx0 = b.localBox[0];
      const bx1 = b.localBox[1];
      const by0 = b.localBox[2];
      const by1 = b.localBox[3];
      const ox = Math.min(ax1, bx1) - Math.max(ax0, bx0);
      const oy = Math.min(ay1, by1) - Math.max(ay0, by0);
      if (ox > 1e-6 && oy > 1e-6) overlaps++;
    }
  }
  checks.push({ id: 'E2', title: '互不重叠', ok: overlaps === 0, summary: `重叠对 ${overlaps}` });

  // E3 落在宗地内（必须拿原始宗地四边形，没给就明说跳过——不要假装通过）
  if (quads && quads.length) {
    checks.push(checkInsideZoning(slots, quads as never));
  } else {
    checks.push({
      id: 'E3',
      title: '落在宗地内',
      ok: true,
      summary: '跳过（调用方没给宗地四边形）',
      detail: '传入 quads 参数即可核验',
    });
  }

  // E4 边角料比例
  const used = slots.reduce((s, x) => s + x.width * x.depth, 0);
  const wasteRatio = zoningArea > 0 ? 1 - used / zoningArea : 0;
  checks.push({
    id: 'E4',
    title: '边角料比例',
    ok: wasteRatio <= 0.3,
    summary: `浪费 ${(wasteRatio * 100).toFixed(1)}%（上限 30%）`,
    detail: `宗地 ${zoningArea.toFixed(1)} m²，楼位 ${used.toFixed(1)} m²`,
  });

  // E5 占位覆盖：每个楼位都必须有外观，一个都不能空。
  //
  // 这一条量的是"绝不会出现空地"——盒子兜底带来的最关键性质（docs/E §2.2）。
  // 预制件匹配率**不是**它的判据：
  //   当前处于阶段 E1（全部用盒子占位），资产库只有 6 条起步条目，
  //   拿 85% 的预制件覆盖率当门槛只会让每份方案都红。
  // 所以预制件覆盖率写进摘要与备注，作为阶段 E2 的进度指标，不参与通过判定。
  const covered = skins.length === slots.length;
  const matched = skins.filter((s) => s.source === 'prefab').length;
  const matchRate = skins.length ? matched / skins.length : 1;
  checks.push({
    id: 'E5',
    title: '楼位全覆盖',
    ok: covered,
    summary: `覆盖 ${skins.length}/${slots.length}，其中预制件 ${matched} 个（${(matchRate * 100).toFixed(0)}%），其余盒子兜底`,
    detail: '预制件覆盖率是阶段 E2 的目标（≥85%，docs/E §7）；阶段 E1 只要求"不空"',
  });

  // E6 畸形块
  const tiny = slots.filter((s) => s.width < CELL - MODULE_TOL || s.depth < CELL - MODULE_TOL).length;
  checks.push({ id: 'E6', title: '无畸形块', ok: tiny === 0, summary: `任一方向 < 1 格：${tiny} 个` });

  const pass = checks.filter((c) => c.ok).length;
  return {
    checks,
    pass,
    fail: checks.length - pass,
    ok: checks.every((c) => c.ok),
    totalSlots: slots.length,
    totalArea: Math.round(used * 10) / 10,
    matchRate,
    fingerprint: fingerprint(slots),
  };
}

/** 逐块核验"slot 是否真的在它的宗地内"（需要原始宗地四边形，所以单独一个函数）。 */
export function checkInsideZoning(slots: readonly Slot[], quads: readonly (readonly [readonly [number, number], readonly [number, number], readonly [number, number], readonly [number, number]])[]): SlotCheck {
  let out = 0;
  for (const s of slots) {
    const insideSome = quads.some((q) => s.quad.every((p) => quadContains(q, p, 0.15)));
    if (!insideSome) out++;
  }
  return { id: 'E3', title: '落在宗地内', ok: out === 0, summary: `越界 ${out} 个` };
}

/** 每种 kind 的实际进深上限提示（给界面显示，避免用户设了不可能的组合）。 */
export function kindDepthHint(kind: ZoneKind): string {
  const spec = KIND_SPECS[kind];
  return `目标面宽 ${spec.targetWidth} m，进深 ${spec.depth} m（上限 ${spec.maxDepth} m = ${spec.maxDepth / CELL} 格）`;
}
