// 生成 Plaza 的 golden 用例（6 primary + 2 stress）。
//
// 用户 2026-11-03 的 P3.0 要求：
//   · **先固定 recipe/settings，只改 geometry / entrance / street context**
//     —— 否则无法判断差异来自"算法"还是"配置"
//   · cohort 从生成时就显式标注，不要以后再按面积猜
//
// Plaza 的 `settings` 实测是**空的**（全部走 `Defaults()`），所以"固定配置"= 一个都不写。
// seed 也统一 1234（与现有 `p90-plaza` 一致），把变量压到只剩几何与街道。
//
// 跑法：node tools/golden/gen-plaza-cases.mjs
// 产物：tools/batch/cases-synth/*.json（与真实烘焙用例分开放，index 里标 source=synth）

import { mkdirSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..');
const OUT = join(ROOT, 'tools', 'batch', 'cases-synth');

if (existsSync(OUT)) rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

/** 保证逆时针（C# 侧假设 ring 为 CCW；反了会让 Area 变成负） */
const ccw = (ring) => {
  let a = 0;
  for (let i = 0; i < ring.length; i++) {
    const p = ring[i];
    const q = ring[(i + 1) % ring.length];
    a += p[0] * q[1] - q[0] * p[1];
  }
  return a >= 0 ? ring : [...ring].reverse();
};

const rect = (x0, z0, x1, z1) => ccw([[x0, z0], [x1, z0], [x1, z1], [x0, z1]]);

/** 绕中心旋转（弧度） */
const rot = (ring, deg) => {
  const t = (deg * Math.PI) / 180;
  const c = Math.cos(t);
  const s = Math.sin(t);
  return ccw(ring.map(([x, z]) => [x * c - z * s, x * s + z * c]));
};

/** 一条街：中心线两端 + 宽度 */
const street = (id, a, b, w = 12, extra = {}) => ({ id, a, b, width: w, category: 'road', sidewalk: true, speed: 50, ...extra });

const CASES = [
  // ── 1. 最基础规则地块：四面临街
  {
    name: 'plaza-r0-rect',
    cohort: 'primary',
    note: '120×100 规则矩形，四面临街。基线用例：不看几何差异，只看 plaza 的基本构成。',
    ring: rect(0, 0, 120, 100),
    streets: [street(1, [0, -6], [120, -6]), street(2, [126, 0], [126, 100]), street(3, [120, 106], [0, 106]), street(4, [-6, 100], [-6, 0])],
  },
  // ── 2. 狭长广场：验证行带/分区在长宽比悬殊时的行为
  {
    name: 'plaza-r1-elongated',
    cohort: 'primary',
    note: '220×45 狭长广场，只在两条长边临街。看关键节点与铺装是否沿长轴组织。',
    ring: rect(0, 0, 220, 45),
    streets: [street(1, [0, -6], [220, -6]), street(2, [220, 51], [0, 51])],
  },
  // ── 3. L 形：`splitRegion` 的第一个真实用例
  {
    name: 'plaza-r2-lshape',
    cohort: 'primary',
    note: 'L 形（横腿 120×50 + 竖腿 50×90）。两条腿的走向互相垂直 —— 这是 splitRegion 存在的理由。',
    ring: ccw([[0, 0], [120, 0], [120, 50], [50, 50], [50, 140], [0, 140]]),
    streets: [
      street(1, [0, -6], [120, -6]),
      street(2, [126, 0], [126, 50]),
      street(3, [-6, 140], [-6, 0]),
      street(4, [0, 146], [50, 146]),
    ],
  },
  // ── 4. 凹形（U）：验证 boolean / 边界处理
  {
    name: 'plaza-r3-concave',
    cohort: 'primary',
    note: 'U 形（120×120 挖掉中间 60×70）。凹口两侧是两条窄臂，路径进不去就是"断头"。',
    ring: ccw([
      [0, 0],
      [120, 0],
      [120, 120],
      [90, 120],
      [90, 50],
      [30, 50],
      [30, 120],
      [0, 120],
    ]),
    streets: [street(1, [0, -6], [120, -6]), street(2, [126, 0], [126, 120]), street(3, [-6, 120], [-6, 0])],
  },
  // ── 5. 多入口：6 个接街口，验证出入口与路径网络的关系
  {
    name: 'plaza-r4-multientrance',
    cohort: 'primary',
    note: '140×110 两侧各 3 个路口（共 6 段街，交错布置）。看是否每个口都被接进铺装。',
    ring: rect(0, 0, 140, 110),
    streets: [
      street(1, [10, -6], [34, -6]),
      street(2, [58, -6], [82, -6]),
      street(3, [106, -6], [130, -6]),
      street(4, [146, 10], [146, 34]),
      street(5, [146, 58], [146, 82]),
      street(6, [146, 96], [146, 110]),
    ],
  },
  // ── 6. 旋转对齐：地块与街道都转 25°，验证朝向选择
  {
    name: 'plaza-r5-rotated',
    cohort: 'primary',
    note: '150×90 矩形整体旋转 25°，街道跟着转。朝向不该被世界坐标轴锁死。',
    ring: rot(rect(0, 0, 150, 90), 25),
    streets: [street(1, ...(() => {
      const t = (25 * Math.PI) / 180;
      const p = ([x, z]) => [x * Math.cos(t) - z * Math.sin(t), x * Math.sin(t) + z * Math.cos(t)];
      return [p([0, -6]), p([150, -6])];
    })()), street(2, ...(() => {
      const t = (25 * Math.PI) / 180;
      const p = ([x, z]) => [x * Math.cos(t) - z * Math.sin(t), x * Math.sin(t) + z * Math.cos(t)];
      return [p([150, 96]), p([0, 96])];
    })())],
  },
  // ── 7. 带障碍物：真实用例的主工况
  //
  // 为什么必须有这一条：真实烘焙的 `p90-plaza` 铺装只有 **53.4%**，而我的规则矩形都是 97~99%。
  // 差异来源查到了 —— 那份输入带 **11 个建筑障碍物**，铺装被 `SiteSafety` 裁掉了 46%。
  // 没有这条用例，就等于在一个"没有建筑"的世界里验证 plaza。
  {
    name: 'plaza-r6-obstacles',
    cohort: 'primary',
    note: '130×110 地块内有 4 栋既有建筑（各 24×18 ~ 40×22）。验证 boolean 挖洞 + SiteSafety 裁铺装。',
    ring: rect(0, 0, 130, 110),
    streets: [street(1, [0, -6], [130, -6]), street(2, [136, 0], [136, 110]), street(3, [-6, 110], [-6, 0])],
    obstacles: [
      { quad: [[18, 20], [42, 20], [42, 38], [18, 38]], kind: 'building' },
      { quad: [[78, 24], [118, 24], [118, 46], [78, 46]], kind: 'building' },
      { quad: [[26, 68], [50, 68], [50, 92], [26, 92]], kind: 'building' },
      { quad: [[86, 72], [112, 72], [112, 94], [86, 94]], kind: 'building' },
    ],
  },
  // ── 8. stress：极小地块
  {
    name: 'plaza-s0-tiny',
    cohort: 'stress',
    note: '约 200 m² 的三角碎地。只验不崩与"不硬塞"。',
    ring: ccw([[0, 0], [24, 0], [0, 18]]),
    streets: [street(1, [0, -6], [24, -6], 8)],
  },
  // ── 8. stress：锐角楔形
  {
    name: 'plaza-s1-acute',
    cohort: 'stress',
    note: '160×14 收成一点的锐角楔形（最窄处 < 1 m）。验 boolean / 内缩在退化几何上的稳健性。',
    ring: ccw([[0, 0], [160, 0], [0, 14]]),
    streets: [street(1, [0, -6], [160, -6], 10)],
  },
];

for (const c of CASES) {
  const obj = {
    generator: 'plaza',
    seed: 1234,
    note: c.note,
    // settings 刻意为空：Plaza 全部走 Defaults()，固定配置只变几何（用户 2026-11-03 的要求）
    settings: {},
    shape: { ring: c.ring },
    streets: c.streets,
    ...(c.obstacles ? { obstacles: c.obstacles } : {}),
  };
  writeFileSync(join(OUT, `${c.name}.json`), JSON.stringify(obj, null, 1) + '\n');
}

// cohort 声明单独落盘（emit 之后再合并进 cohorts.json）
writeFileSync(
  join(HERE, 'plaza-cohorts.json'),
  JSON.stringify(
    {
      protocol: 'site-gen-cohorts/1',
      note: 'Plaza 用例的 cohort 在**生成时**就显式声明（用户 2026-11-03 要求），不按面积事后猜。',
      cases: Object.fromEntries(CASES.map((c) => [c.name, { generator: 'plaza', cohort: c.cohort, note: c.note }])),
    },
    null,
    1,
  ) + '\n',
);

console.log(`写出 ${CASES.length} 份用例 → ${OUT}`);
for (const c of CASES) console.log(`  ${c.cohort.padEnd(8)} ${c.name}`);
