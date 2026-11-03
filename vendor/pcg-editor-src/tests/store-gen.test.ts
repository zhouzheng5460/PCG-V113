import { beforeEach, describe, expect, it } from 'vitest';
import { useEditor } from '../editor/store';
import { ParcelObj, SceneObject } from '../core/model';
import { pathsArea } from '../core/polygon';

/**
 * Phase 5 的"闭环"测试：生成不是算出一堆多边形就完了，
 * 它必须在 store 里表现出正确的一生 —— 生成 → 换一版 → 清除 → 撤销 → 删地块连坐。
 */

const st = () => useEditor.getState();

function reset() {
  st().loadDemo();
  st().showToast('');
}

const parcelOf = (): ParcelObj => {
  const m = st().model;
  return m.order.map((id) => m.objects[id]).find((o) => o.kind === 'parcel') as ParcelObj;
};

const generatedOf = (parcelId: string): SceneObject[] => {
  const m = st().model;
  return m.order
    .map((id) => m.objects[id])
    .filter((o): o is SceneObject => !!o && o.kind === 'building' && o.generatedFrom === parcelId);
};

const buildingCount = () =>
  st().model.order.reduce((n, id) => (st().model.objects[id]?.kind === 'building' ? n + 1 : n), 0);

describe('store · 地块生成闭环', () => {
  beforeEach(reset);

  it('生成：写入 generated 建筑、记录统计、选中留在地块上', () => {
    const parcel = parcelOf();
    const fixedBefore = buildingCount() - generatedOf(parcel.id).length;

    st().generateParcel(parcel.id);

    const made = generatedOf(parcel.id);
    expect(made.length).toBeGreaterThan(0);
    for (const b of made) {
      expect(b.kind).toBe('building');
      if (b.kind !== 'building') continue;
      expect(b.origin).toBe('generated');
      expect(b.name).toContain('厂房'); // 示例地块是工业用地
    }
    // 手绘 / 固定建筑一栋没少
    expect(buildingCount() - made.length).toBe(fixedBefore);
    expect(st().genStats[parcel.id]).toBeTruthy();
    expect(st().view.selectionIds).toEqual([parcel.id]);
  });

  it('同一个 seed 再生成一次 = 原地重算，不是叠加', () => {
    const parcel = parcelOf();
    st().generateParcel(parcel.id);
    const first = generatedOf(parcel.id);
    const sig1 = first
      .map((b) => (b.kind === 'building' ? Math.abs(pathsArea(b.footprint)).toFixed(2) : ''))
      .join('|');

    st().generateParcel(parcel.id);
    const second = generatedOf(parcel.id);
    const sig2 = second
      .map((b) => (b.kind === 'building' ? Math.abs(pathsArea(b.footprint)).toFixed(2) : ''))
      .join('|');

    expect(second.length).toBe(first.length); // 不是 2 倍
    expect(sig2).toBe(sig1); // 确定性
  });

  it('换一版：seed 递增，产物跟着变，旧的一栋不留', () => {
    const parcel = parcelOf();
    st().generateParcel(parcel.id);
    const before = generatedOf(parcel.id).map((b) => b.id);

    st().generateParcel(parcel.id, true);

    const p2 = parcelOf();
    expect(p2.seed).toBe(parcel.seed + 1);
    const after = generatedOf(parcel.id);
    expect(after.length).toBeGreaterThan(0);
    // 旧的 id 全部消失（是覆盖，不是并存）
    for (const id of before) expect(st().model.objects[id]).toBeUndefined();
  });

  it('清除生成：只删 generated，手绘建筑与地块本身都在', () => {
    const parcel = parcelOf();
    st().generateParcel(parcel.id);
    const keptBuildings = (st().model.order || []).filter((id) => {
      const o = st().model.objects[id];
      return o?.kind === 'building' && o.generatedFrom !== parcel.id;
    }).length;

    st().clearGenerated(parcel.id);

    expect(generatedOf(parcel.id)).toHaveLength(0);
    expect(st().model.objects[parcel.id]).toBeTruthy();
    const remainingBuildings = st().model.order.filter((id) => st().model.objects[id]?.kind === 'building').length;
    expect(remainingBuildings).toBe(keptBuildings);
    expect(st().genStats[parcel.id]).toBeUndefined();
  });

  it('撤销：一次生成 = 一步历史', () => {
    const parcel = parcelOf();
    const before = st().model.order.length;
    st().generateParcel(parcel.id);
    expect(st().model.order.length).toBeGreaterThan(before);

    st().undo();
    expect(st().model.order.length).toBe(before);
    expect(generatedOf(parcel.id)).toHaveLength(0);
  });

  it('删地块连坐：它生成的建筑一起走，不留孤儿', () => {
    const parcel = parcelOf();
    st().generateParcel(parcel.id);
    expect(generatedOf(parcel.id).length).toBeGreaterThan(0);

    st().select([parcel.id]);
    st().deleteSelected();

    expect(st().model.objects[parcel.id]).toBeUndefined();
    const orphans = st().model.order.filter((id) => {
      const o = st().model.objects[id];
      return o?.kind === 'building' && o.generatedFrom === parcel.id;
    });
    expect(orphans).toHaveLength(0);
    expect(st().genStats[parcel.id]).toBeUndefined();
  });

  it('没有地块时点生成 → 只提示，不崩不脏写', () => {
    st().newProject();
    st().showToast('');
    st().generateParcel();
    expect(st().toast).toContain('地块');
    expect(st().past).toHaveLength(0); // 没有产生历史 = 没改模型
  });
});
