import { Bounds } from '../core/polygon';

/** 动态对齐参考线（PRD §29~§30） */
export interface Guide {
  /** 'x' = 竖直线（x 固定），'y' = 水平线（y 固定） */
  axis: 'x' | 'y';
  value: number;
  from: number;
  to: number;
  kind: 'align' | 'manual';
  label?: string;
}

interface Box {
  bounds: Bounds;
  id: string;
}

/** 边缘 / 中心对齐 */
export function alignmentGuides(moving: Bounds, others: Box[], tol: number): Guide[] {
  const out: Guide[] = [];
  const mA = [moving.minX, (moving.minX + moving.maxX) / 2, moving.maxX];
  const mB = [moving.minY, (moving.minY + moving.maxY) / 2, moving.maxY];

  for (const o of others) {
    const b = o.bounds;
    const oA = [b.minX, (b.minX + b.maxX) / 2, b.maxX];
    const oB = [b.minY, (b.minY + b.maxY) / 2, b.maxY];

    for (const a of mA) {
      for (const t of oA) {
        if (Math.abs(a - t) <= tol) {
          out.push({
            axis: 'x',
            value: t,
            from: Math.min(moving.minY, b.minY),
            to: Math.max(moving.maxY, b.maxY),
            kind: 'align',
          });
        }
      }
    }
    for (const a of mB) {
      for (const t of oB) {
        if (Math.abs(a - t) <= tol) {
          out.push({
            axis: 'y',
            value: t,
            from: Math.min(moving.minX, b.minX),
            to: Math.max(moving.maxX, b.maxX),
            kind: 'align',
          });
        }
      }
    }
  }
  return dedupe(out);
}

function dedupe(guides: Guide[]): Guide[] {
  const seen = new Map<string, Guide>();
  for (const g of guides) {
    const key = `${g.axis}:${g.value.toFixed(3)}`;
    const prev = seen.get(key);
    if (!prev) seen.set(key, g);
    else prev.from = Math.min(prev.from, g.from), (prev.to = Math.max(prev.to, g.to));
  }
  return [...seen.values()];
}

/** 平移吸附到参考线：返回吸附后的位移修正 */
export function guideSnapDelta(moving: Bounds, others: Box[], tol: number): { dx: number; dy: number } {
  let dx = 0;
  let dy = 0;
  let bestX = tol;
  let bestY = tol;
  const mA = [moving.minX, (moving.minX + moving.maxX) / 2, moving.maxX];
  const mB = [moving.minY, (moving.minY + moving.maxY) / 2, moving.maxY];

  for (const o of others) {
    const b = o.bounds;
    const oA = [b.minX, (b.minX + b.maxX) / 2, b.maxX];
    const oB = [b.minY, (b.minY + b.maxY) / 2, b.maxY];
    for (const a of mA) for (const t of oA) {
      const d = t - a;
      if (Math.abs(d) <= bestX) {
        bestX = Math.abs(d);
        dx = d;
      }
    }
    for (const a of mB) for (const t of oB) {
      const d = t - a;
      if (Math.abs(d) <= bestY) {
        bestY = Math.abs(d);
        dy = d;
      }
    }
  }
  return { dx, dy };
}
