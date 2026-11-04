// 本地 C# 服务客户端（方案 B）。
//
// 为什么需要它：编辑器里「楼的来源 = Placemaker」原本是 `bridge/` 里的 **TS 复刻**。
// 复刻永远是近似的。要跑**真 C# 算法**，就通过这个薄客户端调本机服务：
//
//   dotnet tools/oracle/driver/bin/Release/net6.0/PlacemakerDriver.dll --serve 8787
//
// 契约（见 tools/oracle/driver/Serve.cs）：
//   GET  /health  → { ok, service, protocol, planProtocol, generators[] }
//   POST /plan    → body = case JSON，返回 plan JSON（placemaker-plan/1）
//
// 本文件只做三件事：探活、组装 case、取 plan。**不做任何几何** ——
// 几何在 C# 那边，这边跟着它的口径走才不会分叉。

import { Pt } from '../core/geom';
import { UNIT_M } from '../core/units';
import type { Model } from '../town/model';
import type { Patch } from '../town/patch';

export const SERVICE_URL = 'http://127.0.0.1:8787';

export interface LiveStatus {
  ok: boolean;
  ms: number;
  /** 服务自报的生成器数量（130 个左右） */
  generators?: string[];
  error?: string;
}

/** 探活：2 秒超时，失败不抛（编辑器要能在"服务没起"时正常用） */
export async function health(url = SERVICE_URL): Promise<LiveStatus> {
  const t0 = performance.now();
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 2000);
    const res = await fetch(`${url}/health`, { signal: ctrl.signal });
    clearTimeout(timer);
    if (!res.ok) return { ok: false, ms: performance.now() - t0, error: `HTTP ${res.status}` };
    const j = (await res.json()) as { ok?: boolean; generators?: string[] };
    return { ok: !!j.ok, ms: performance.now() - t0, generators: j.generators };
  } catch (e) {
    return { ok: false, ms: performance.now() - t0, error: (e as Error).message };
  }
}

/** case JSON 的形状（与 tools/oracle/driver/Emit.cs 的读法一致） */
export interface PlanCase {
  generator: string;
  seed: number;
  settings: Record<string, unknown>;
  shape: { ring: [number, number][] };
  streets: { id: number; a: [number, number]; b: [number, number]; width: number; category?: string }[];
}

const round = (v: number): number => Math.round(v * 1000) / 1000;
/** 世界单位 → 米。**跨到 C# 必须换**：Placemaker 全程按米，直接发单位会让地块缩成 1/7，
 *  于是 EntranceSuggest 找不到临街面、CheckReady 报 NeedsEntrance（实测就是这样）。 */
const m = (v: number): number => round(v * UNIT_M);

/**
 * 把一个地块转成 case。
 *
 * 约定：TF 的街区边界**就是道路中心线**（道路按中心线向两侧展开宽度），
 * 所以地块的每条边可以直接当 `streets` 的一条，宽度取该等级的断面红线宽。
 * 这条映射是这套系统里唯一"必须对齐"的地方 —— 对不上就会得到"临街但没路"的地块。
 */
export function caseFromPatch(model: Model, patch: Patch, generator = 'parking'): PlanCase {
  const ring = (patch.shape as unknown as Pt[]).map((v) => [m(v.x), m(v.y)] as [number, number]);
  const streets: PlanCase['streets'] = [];
  let id = 1;
  patch.shape.forEdge((v0, v1) => {
    const w = Math.max(2, model.edgeWidthAt(v0, v1)) * UNIT_M;
    streets.push({
      id: id++,
      a: [m(v0.x), m(v0.y)],
      b: [m(v1.x), m(v1.y)],
      width: round(w),
      category: 'road',
    });
  });
  return { generator, seed: 1234, settings: {}, shape: { ring }, streets };
}

export interface PlanResult {
  ok: boolean;
  ms: number;
  error?: string;
  plan?: any;
}

/** 调 /plan。失败返回 ok:false，不抛 */
export async function planCase(c: PlanCase, url = SERVICE_URL): Promise<PlanResult> {
  const t0 = performance.now();
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 15000);
    const res = await fetch(`${url}/plan`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
      signal: ctrl.signal,
    });
    clearTimeout(timer);
    const j: any = await res.json();
    const ms = performance.now() - t0;
    if (!res.ok || j?.ok === false) {
      return { ok: false, ms, error: j?.error ?? `HTTP ${res.status}` };
    }
    return { ok: true, ms, plan: j };
  } catch (e) {
    return { ok: false, ms: performance.now() - t0, error: (e as Error).message };
  }
}

/** 一份 plan 的要素计数（面板与提示共用） */
export function planCounts(plan: any): { sur: number; net: number; obj: number; zon: number; ms: number } {
  return {
    sur: plan?.surfaces?.length ?? 0,
    net: plan?.nets?.length ?? 0,
    obj: plan?.objects?.length ?? 0,
    zon: plan?.zoning?.length ?? 0,
    ms: plan?.elapsedMs ?? 0,
  };
}
