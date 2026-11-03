// 两个后端，一个接口。这是整个迁移的结构性保证：
// "原生实现等价于 Placemaker" 之所以能被验证，是因为 golden 和 native 走同一个函数签名，
// 吃同一份输入，产出同一种 Plan。
//
//   goldenBackend  —— 读 `tools/golden/plans/*.plan.json`（零 dotnet，毫秒级）
//   nativeBackend  —— 查注册表调 TS 生成器；没注册的返回 has()===false
//
// **没有第三个后端。** 任何"临时把 C# 的输出缓存一下混进去"的做法都会让比对失真。

import type { BackendName, GeneratorBackend, Plan, SiteGenerator, SiteInput } from '../types.ts';
import { loadGoldenPlan } from './golden.ts';

/**
 * golden 后端：只读快照。
 *
 * 为什么不在运行时调 dotnet：比对要在**每次改动后**跑，几百毫秒 vs 几秒差一个数量级；
 * 而且快照是逐字节稳定验证过的（`emit.mjs` 跑两遍比对），比"现场再算一遍"更可信——
 * 现场算会把 oracle 自身的不确定性带进比较里。
 * 需要重烘时才跑 `node tools/golden/emit.mjs`。
 */
export function goldenBackend(available: Set<string>): GeneratorBackend {
  return {
    name: 'golden' as BackendName,
    has: (id) => available.has(id),
    generate: (_id, _input) => {
      // 忽略入参：快照就是"这份输入 → 那份输出"的完整记录
      return loadGoldenPlan(_id);
    },
  };
}

/**
 * 原生后端：查注册表。
 *
 * `has()` 必须诚实返回 false —— 没实现就说没实现。
 * 返回一个空 Plan 会被比对器当成"形状对、数量全 0"，从而报出"覆盖率 0%，差 100%"这种
 * 看着有信息其实毫无意义的结论，还会掩盖"这个生成器根本没写"。
 */
export function nativeBackend(generators: readonly SiteGenerator[]): GeneratorBackend {
  const map = new Map(generators.map((g) => [g.id, g]));
  return {
    name: 'native',
    has: (id) => map.has(id),
    generate: (id, input) => {
      const g = map.get(id);
      if (!g) throw new Error(`原生后端没有生成器 "${id}"`);
      return g.generate(input);
    },
  };
}

/** 一个空 Plan 骨架：通道全部存在且为空。原生生成器从这里开始，避免漏通道。 */
export function emptyPlan(input: SiteInput): Plan {
  return {
    protocol: 'placemaker-plan/1',
    generator: input.generator,
    units: 'meters',
    seed: input.seed,
    area: 0,
    ready: true,
    markers: { given: 0, auto: input.markers.length },
    settings: input.settings,
    surfaces: [],
    nets: [],
    objects: [],
    areas: [],
    regions: [],
    zoning: [],
    paint: [],
    terraform: [],
    stats: [],
    issues: [],
  };
}
