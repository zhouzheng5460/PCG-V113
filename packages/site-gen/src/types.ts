// Native Web Site Generator —— 契约层。
//
// 这一层的存在理由，是把 C# 从"生成链的一环"降级成"只在需要重烘时才跑的 oracle"：
//
//   以前：TownForge → 启动 dotnet → Placemaker C# → Plan JSON → Scene IR
//   现在：TownForge → SiteGenerator → Plan JSON → Scene IR
//                            ├── PlacemakerBackend（C#，golden oracle，离线）
//                            └── NativeWebBackend（TS，正式生成链）
//
// 关键决定：**Plan JSON 是唯一契约，不新增中间协议。**
// `Plan`（`packages/plan-ir/src/types.ts`）已经有 `protocol: 'placemaker-plan/1'` 和 6 个通道
// ——`surfaces / nets / objects / zoning / paint / terraform`——外加 `issues` 与 `ready`。
// 后端之间只交换这一个结构，所以"原生实现是否等价于 Placemaker"是一个**纯函数比对**问题：
// 同一个 `CaseInput` 喂两个后端，比两份 `Plan`。
//
// 为什么不按你说的 8 个通道：我先前只看了 TS 侧的 `Plan` 类型，**说错了一次**。
// 实际 `--emit` 产出的 JSON 有 **9 条通道**——除了 `surfaces / nets / objects / zoning / paint /
// terraform`，还真的有 `areas`（生成器的工作分区，parking 是 1 条 `lot`）和
// `regions`（具名区域 + tags，parking 是 44 条 `lot:N` / `green:N`）。
// 另有 `stats`（生成器自报的 `Stat.Stalls=288 / Stat.AisleLength=1102`）和 `facts`。
// 现在 `packages/plan-ir/src/types.ts` 已补齐 `PlanArea / PlanRegion / PlanStat`，
// 通道清单一律以**实际 JSON** 为准，不以类型文件为准（D-058）。

import type { CaseInput, Plan } from '../../plan-ir/src/types.ts';
import type { Vec2 } from '../../plan-ir/src/geom.ts';

export type { CaseInput, Plan };

/**
 * Plan 的通道清单。冻结契约：少一个、多一个都要在这里改。
 *
 * `stats` 也是通道之一：它是 **oracle 自己数出来的数**（车位数 / 车道总长 / 树数），
 * 比我从几何反算更可信，所以必须进契约、必须参与比对。
 */
export const CHANNELS = [
  'surfaces',
  'nets',
  'objects',
  'areas',
  'regions',
  'zoning',
  'paint',
  'terraform',
  'stats',
  'issues',
] as const;
export type ChannelName = (typeof CHANNELS)[number];

export const PLAN_PROTOCOL = 'placemaker-plan/1';

/**
 * **oracle 解析之后的输入**（`placemaker-input/1`）。
 *
 * 这是原生生成器唯一应当接受的输入形态 —— 不是 `CaseInput`，理由见 D-059：
 *
 * `CaseInput` 是"用例原文 + TS 侧自己的解析"（`toCaseInput`）。但同一份用例，C# 那边还会做三件事：
 * 把 `settings` 与 `gen.Defaults()` 合并（parking 有 **33 项**）、把街段按三等分算出 `b/c` 控制点、
 * 让 `SuggestMarkers` 补出入口。TS 侧再解析一遍，比的就成了"两份解析器谁解析得对"，
 * 而不是"两份生成器谁画得对"。
 *
 * 所以约定：**喂给原生后端的输入 = C# 导出的 `.input.json`**。解析只在 C# 里发生一次。
 * 代价是原生链路在过渡期依赖 oracle 产出的输入文件；等 TS 侧解析器被验证等价之后，
 * 再把解析搬回来（那时它就是一个有 golden 的普通模块，而不是一个没测过的假设）。
 */
export interface SiteInput {
  protocol: 'placemaker-input/1';
  generator: string;
  seed: number;
  shape: { ring: [number, number][] };
  layoutScale: number;
  edgeMargin: number;
  nearbyNets: SiteNet[];
  zoneGrids: { center: [number, number]; direction: [number, number]; nx: number; ny: number }[];
  obstacles: { quad: [[number, number], [number, number], [number, number], [number, number]]; kind: string }[];
  settings: Record<string, number | boolean | string | string[]>;
  markers: { kind: string; ringSegment: number; ringT: number; position: [number, number]; crosswalk: boolean }[];
  regionTags: string[];
  /** `water` 的原始块（C# 里是闭包，导不出函数，给原始参数让 TS 复现同一个判定） */
  water: { axis?: string; at?: number; depth?: number; side?: string } | null;
}

export interface SiteNet {
  externalId: number;
  a: Vec2;
  b: Vec2;
  c: Vec2;
  d: Vec2;
  width: number;
  category: 'road' | 'pathway' | 'track' | 'other';
  hasSidewalk: boolean;
  isOneWay: boolean;
  isHighway: boolean;
  speedLimit: number;
}

/**
 * 生成器能力声明。
 *
 * 为什么每个生成器都要声明：原生实现最容易犯的错不是"画歪了"，而是"静默少写一个通道"
 * ——看起来正常，下游却少了车位线或高程。声明了 `channels` 之后，比对器可以检查
 * "golden 在这个通道有条目，而你没有"这类**沉默缺失**（见 `compare.ts` 的 `silentLoss`）。
 */
export interface GeneratorCaps {
  /** 这个生成器会写哪些通道（其余应当留空） */
  channels: readonly ChannelName[];
  /** 是否依赖边界出入口才能 work（`ready` 的判据之一，见 `packages/street-rules`） */
  usesEntrances: boolean;
  /** 最小地块面积（m²），小于它的地块不生成 */
  minArea: number;
  /** 一句话说明它是从哪几个「原语」拼出来的（给人看，不参与计算） */
  builtFrom: readonly string[];
}

/**
 * 原生 Web 生成器 —— 整个迁移过程唯一的扩展点。
 *
 * 刻意**不用类**：`generate` 必须是纯函数。这一路踩过的坑（D-029 / D-037 / D-041）
 * 有一半是"状态在两次调用之间泄漏"，纯函数让这类问题不可能发生。
 */
export interface SiteGenerator {
  readonly id: string;
  readonly caps: GeneratorCaps;
  generate(input: SiteInput): Plan;
}

/** 后端标识 */
export type BackendName = 'golden' | 'native';

/**
 * 生成后端。
 *
 * `has` 必须诚实：原生后端在某个生成器还没实现时必须返回 false，
 * 而不是返回一个空 Plan —— 空 Plan 会被比对器当成"全 0 但形状正确"，从而得出
 * "覆盖率 0%，差距 100%"这种看似有信息其实没意义的结论。
 */
export interface GeneratorBackend {
  readonly name: BackendName;
  has(id: string): boolean;
  generate(id: string, input: SiteInput): Plan;
}
