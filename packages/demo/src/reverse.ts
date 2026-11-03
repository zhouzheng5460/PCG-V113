// 反向验证：街道契约能不能**预测** Placemaker 的 ready 判定。
//
// 为什么这是 P1 的最后一关（docs/A 验收门最后一行）：
// 前面所有验证都是"我们的贴合点算得对不对"——那只是模仿得像。
// 真正要回答的是："我们的输入契约能不能预测引擎会不会收下这块地"。
// 如果 R1/R7 说的"没临街/没有车行可达"和 C# 的 ready=false + NeedsEntrance
// 一一对上，那这套契约就不只是格式正确，而是**有预测力**。
//
// 做法（零推理，纯对照）：
//   对 54 份真实烘焙输入，各自真跑一遍 `dotnet --emit`，记录 ready / readyKey / 各通道数量；
//   再用我们的 validateCase 算 R1/R7/临街边数；最后交叉制表。
//
// 跑法：node packages/demo/src/reverse.ts           全跑（约 1 分钟）
//       node packages/demo/src/reverse.ts --no-emit  只读上次结果，不重跑 dotnet
// 产物：packages/demo/out/reverse.html + tmp/reverse/*.json

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { area, signedArea, type Vec2 } from '../../plan-ir/src/geom.ts';
import { validateCase } from '../../street-rules/src/rules.ts';
import { netFromSegment } from '../../street-rules/src/finder.ts';
import { entranceReachable } from '../../street-rules/src/entrance.ts';
import { entranceClass, predictReady, streetCanAffectReady, type EntranceClass } from '../../street-rules/src/ready.ts';
import type { CaseInput, NearbyNet } from '../../plan-ir/src/types.ts';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const caseDir = join(root, 'tools/batch/cases');
const outDir = join(root, 'packages/demo/out');
const runDir = join(root, 'tmp/reverse');
const dll = join(root, 'tools/oracle/driver/bin/Release/net6.0/PlacemakerDriver.dll');

const noEmit = process.argv.includes('--no-emit');

interface RawCase {
  generator: string;
  seed: number;
  shape: { ring: [number, number][] };
  streets: { id: number; a: [number, number]; b: [number, number]; width: number; category?: string }[];
  /** 烘焙工具塞进去的给定出入口（商场/市场族常见），有它 ready 就与街道无关了。 */
  markers?: { kind: string; at: [number, number] }[];
  settings?: Record<string, unknown>;
}

interface RawPlan {
  generator: string;
  ready: boolean;
  readyKey?: string;
  area: number;
  markers?: { given: number; auto: number };
  surfaces: unknown[];
  /** 每条是一条贝塞尔；startAttach/endAttach 为 -1 表示没接到街。 */
  nets: { role?: string; startAttach?: number; endAttach?: number }[];
  objects: unknown[];
  areas: unknown[];
  zoning: unknown[];
  paint: unknown[];
  terraform: unknown[];
  facts?: Record<string, unknown>;
  issues?: unknown[];
}

interface Row {
  name: string;
  generator: string;
  areaM2: number;
  edges: number;
  streets: number;
  alongEdges: number;
  /** 我们的判定：有没有任何一条边临街（R1）。 */
  r1ok: boolean;
  /** 我们的判定：有没有车行可达的临街边（R7）。 */
  r7ok: boolean;
  /** 我们的判定：总判定。 */
  contractOk: boolean;
  /** 我们的判定：parking 族能否从街上建议出口（EntranceSuggest 准入）。 */
  entOk: boolean;
  /** 真实 case 里是否已经带了给定出入口 —— 有的话 ready 与街道无关。 */
  hasGivenMarker: boolean;
  /** 引擎视角：输入里给了几个口 / 建议器补了几个。 */
  given: number;
  auto: number;
  /** 出入口家族（决定 ready 判据）。 */
  family: EntranceClass;
  /** 我们的预测：引擎会不会 ready。null = 判不了。 */
  predReady: boolean | null;
  predBy: string;
  entNote: string;
  /** 我们的判定：临街但太短（R11 口径）。 */
  shortFrontage: number;
  /** C# 的判定。 */
  ready: boolean;
  readyKey: string;
  reason: string;
  /** C# 各通道数量。 */
  counts: { surfaces: number; nets: number; objects: number; areas: number; zoning: number; paint: number; terraform: number };
  /** plan 里接到了街上的路径条数 / 总路径条数（量 TrimOutlineEnds 有没有生效）。 */
  attachedNets: number;
  totalNets: number;
  /** 是否真跑成功。 */
  ran: boolean;
  err?: string;
}

const pct = (a: number, b: number): string => (b === 0 ? '—' : `${((a / b) * 100).toFixed(1)}%`);

/** readyKey 形如 "Placemaker.Parking.Step.PlaceEntrance|Placemaker.Parking.Reason.NeedsEntrance"。
 *  取最后一段，并把 `Reason.` 之前的命名空间前缀去掉，剩下 `Reason.NeedsEntrance`。 */
const reasonOf = (key: string): string => {
  if (!key) return '';
  const last = key.split('|').pop() ?? key;
  const i = last.lastIndexOf('Reason.');
  return i >= 0 ? last.slice(i) : last;
};

/** Reason.NeedsEntrance → 中文（docs/C 的 ready-reason 表只覆盖了一部分，这里按需补）。 */
const REASON_CN: Record<string, string> = {
  'Reason.NeedsEntrance': '需要一个出入口',
  'Reason.NeedsRoad': '需要临街',
  'Reason.NeedsAccess': '需要车辆可达',
  'Reason.TooSmall': '地块太小',
  'Reason.AreaTooSmall': '面积不足',
  'Reason.NoFit': '放不下',
  'Reason.Blocked': '被挡死',
  'Reason.NeedsGround': '需要已有地面',
};

function toCase(raw: RawCase): { input: CaseInput; ring2d: Vec2[]; nets: NearbyNet[] } {
  let ring2d: Vec2[] = raw.shape.ring.map((p) => [p[0], p[1]] as Vec2);
  if (signedArea(ring2d) < 0) ring2d = [...ring2d].reverse();
  const nets = raw.streets.map((s) =>
    netFromSegment(s.id, s.a, s.b, s.width, { category: (s.category as never) ?? 'road' }),
  );
  const input: CaseInput = {
    shape: { ring: ring2d.map((p) => [p[0], 0, p[1]] as [number, number, number]), source: 'block', attachments: [] },
    site: { nearbyNets: nets, obstacles: [], zoneGrids: [], layoutScale: 0.5, edgeMargin: 2 },
    markers: [],
    settings: {},
    seed: raw.seed,
    regionTags: ['root'],
  };
  return { input, ring2d, nets };
}

function main(): void {
  if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });
  if (!existsSync(runDir)) mkdirSync(runDir, { recursive: true });

  const names = readdirSync(caseDir).filter((f) => f.endsWith('.json')).sort();
  const rows: Row[] = [];

  console.log('══ 反向验证：契约判定 vs 引擎判定 ══');
  console.log(`  夹具 ${names.length} 份${noEmit ? '（--no-emit，读上次结果）' : ''}`);

  for (const name of names) {
    const raw = JSON.parse(readFileSync(join(caseDir, name), 'utf8')) as RawCase;
    const { input, ring2d, nets } = toCase(raw);
    const rep = validateCase(input);
    const r1 = rep.rules.find((x) => x.id === 'R1');
    const r7 = rep.rules.find((x) => x.id === 'R7');
    const family = entranceClass(raw.generator);
    // 只有 parking 族的 ready 真受街道影响，别的家族算这个纯属浪费。
    const ent = family === 'parking' ? entranceReachable(ring2d, nets) : null;

    const id = name.replace(/\.json$/, '');
    const planPath = join(runDir, `${id}.json`);
    let plan: RawPlan | null = null;
    let err: string | undefined;

    if (!noEmit) {
      try {
        execFileSync('dotnet', [dll, '--emit', join(caseDir, name), planPath], {
          encoding: 'utf8',
          stdio: ['ignore', 'pipe', 'pipe'],
        });
      } catch (e) {
        err = (e as Error).message.split('\n')[0];
      }
    }
    if (!err && existsSync(planPath)) {
      try {
        plan = JSON.parse(readFileSync(planPath, 'utf8')) as RawPlan;
      } catch (e) {
        err = `读不出 plan：${(e as Error).message}`;
      }
    }

    const readyKey = plan?.readyKey ?? '';
    // 引擎自报的 given/auto 比我们自己数 markers 可靠：它区分了"输入给的"和"建议补的"。
    const given = plan?.markers?.given ?? raw.markers?.length ?? 0;
    const auto = plan?.markers?.auto ?? 0;
    const pred = predictReady({ generator: raw.generator, givenMarkers: given, suggestOk: ent?.reachable ?? false });
    const nlist = plan?.nets ?? [];
    const attachedNets = nlist.filter((n) => (n.startAttach ?? -1) >= 0 || (n.endAttach ?? -1) >= 0).length;

    rows.push({
      name: id,
      generator: raw.generator,
      areaM2: area(ring2d),
      edges: ring2d.length,
      streets: nets.length,
      alongEdges: rep.edges.filter((e) => e.along.length > 0).length,
      r1ok: r1?.ok ?? false,
      r7ok: r7?.ok ?? false,
      contractOk: rep.ok,
      entOk: ent?.reachable ?? false,
      entNote: ent?.note ?? '',
      hasGivenMarker: (raw.markers?.length ?? 0) > 0,
      given,
      auto,
      family,
      predReady: pred.ready,
      predBy: pred.by,
      shortFrontage: rep.edges.filter((e) => (e.along.length > 0 || e.nearMiss) && e.length < 12).length,
      ready: plan?.ready ?? false,
      readyKey,
      reason: reasonOf(readyKey),
      counts: {
        surfaces: plan?.surfaces?.length ?? 0,
        nets: nlist.length,
        objects: plan?.objects?.length ?? 0,
        areas: plan?.areas?.length ?? 0,
        zoning: plan?.zoning?.length ?? 0,
        paint: plan?.paint?.length ?? 0,
        terraform: plan?.terraform?.length ?? 0,
      },
      attachedNets,
      totalNets: nlist.length,
      ran: !!plan,
      err,
    });
  }

  // ─────────────────────────────── 交叉制表

  const ranRows = rows.filter((r) => r.ran);
  console.log(`  引擎真跑成功 ${ranRows.length}/${rows.length}`);
  const csharpReady = ranRows.filter((r) => r.ready).length;
  console.log(`  引擎 ready=yes ${csharpReady}｜no ${ranRows.length - csharpReady}`);

  const reasons = new Map<string, number>();
  for (const r of ranRows) if (!r.ready) reasons.set(r.reason || '(空)', (reasons.get(r.reason || '(空)') ?? 0) + 1);
  for (const [k, v] of [...reasons.entries()].sort((a, b) => b[1] - a[1])) {
    console.log(`    ${k} ${REASON_CN[k] ? `（${REASON_CN[k]}）` : ''} — ${v} 份`);
  }

  // ─── 家族分布：ready 的判据按家族分家，先看这 54 份落在哪几个家族 ───
  const byFamily = new Map<EntranceClass, Row[]>();
  for (const r of ranRows) {
    const a = byFamily.get(r.family) ?? [];
    a.push(r);
    byFamily.set(r.family, a);
  }
  console.log('');
  console.log('  ready 判据按家族分家（family → 份数 / 其中 ready=yes / 是否受街道影响）：');
  for (const [f, rs] of [...byFamily.entries()].sort((a, b) => b[1].length - a[1].length)) {
    const y = rs.filter((r) => r.ready).length;
    const street = f === 'parking' || f === 'typed' ? '街道能影响' : '街道**不可能**影响';
    console.log(`    ${f.padEnd(9)} ${String(rs.length).padStart(3)} 份   ready ${String(y).padStart(3)}    ${street}`);
  }

  // ─── 主要指标：预测 vs 引擎 ───
  const judged = ranRows.filter((r) => r.predReady !== null);
  const hit = judged.filter((r) => r.predReady === r.ready);
  const miss = judged.filter((r) => r.predReady !== r.ready);
  console.log('');
  console.log(`  预测 ready：${hit.length}/${judged.length} 一致（${pct(hit.length, judged.length)}），判不了 ${ranRows.length - judged.length} 份`);
  if (miss.length) {
    console.log('  ✗ 预测错的：');
    for (const r of miss) console.log(`    ${r.name.padEnd(36)} ${r.generator.padEnd(14)} 预测=${r.predReady} 实际=${r.ready}  ${r.reason}`);
  }

  // 四格表喂给报告（"我们预测收下/拒收" × "引擎实际"）
  const quad = {
    a: judged.filter((r) => r.predReady === true && r.ready).length,
    b: judged.filter((r) => r.predReady === true && !r.ready).length,
    c: judged.filter((r) => r.predReady === false && r.ready).length,
    d: judged.filter((r) => r.predReady === false && !r.ready).length,
  };

  // ─── 对照：把"街道契约"当 ready 判据会怎样（说明它不该这么用）───
  console.log('');
  console.log('  对照：若拿街道契约当 ready 判据（前一轮的错误做法）：');
  for (const [label, pick] of [
    ['R1 有任何临街边', (r: Row) => r.r1ok],
    ['R7 车行可达', (r: Row) => r.r7ok],
    ['出入口可达（EntranceSuggest 准入）', (r: Row) => r.entOk],
  ] as const) {
    const ok = ranRows.filter((r) => pick(r) === r.ready).length;
    console.log(`    ${label.padEnd(34)} 一致 ${ok}/${ranRows.length}`);
  }
  console.log('  （R1/R7 是"街道够不够用"，不是"引擎收不收这块地"，两件事。）');

  // ─── 街道真正的作用点：ready=no 时路径不许接街 ───
  const noReady = ranRows.filter((r) => !r.ready);
  const yesReady = ranRows.filter((r) => r.ready);
  const attNo = noReady.reduce((s, r) => s + r.attachedNets, 0);
  const attYes = yesReady.reduce((s, r) => s + r.attachedNets, 0);
  console.log('');
  console.log('  街道真正的落点（LayerBase.cs:521-531 TrimOutlineEnds）：');
  console.log(`    ready=no  的 ${noReady.length} 份：接到街的路径 ${attNo} 条（应恒为 0）`);
  console.log(`    ready=yes 的 ${yesReady.length} 份：接到街的路径 ${attYes} 条`);

  // 关键切分：自带给定出入口的那批，ready 与街道无关，不能算进"预测力"
  const withMarker = ranRows.filter((r) => r.hasGivenMarker);
  console.log('');
  console.log(`  其中 ${withMarker.length} 份**自带给定出入口**（烘焙工具塞的），ready 与街道无关：`);
  for (const r of withMarker) console.log(`    ${r.name.padEnd(36)} ${r.generator.padEnd(14)} ${r.family.padEnd(9)} given=${r.given} → ready=${r.ready}`);

  // ─────────────────────────────── 三 · 扰动实验（造负例）
  //
  // 上一步的 54/54 有个大问题：真实数据 53 收 1 拒，样本极度不平衡，
  // 100% 一致只证明了"那唯一一份没临街的确实被拒了"，**区分力没被检验**。
  // 所以这里拿同一块真实地块，人为造出几种"应该被拒"的变体，看判据跟不跟得上。

  console.log('');
  console.log('══ 扰动实验：给真实地块造负例 ══');
  console.log('  目的不是"证明判据对"，而是看**断言本身**成不成立：');
  console.log('  shop/market 族应该"街道怎么改都不动"；parking 族应该"街道一动就变"。');
  const pert = runPerturbations();
  for (const g of pert) {
    const fam = g.variants[0]?.family ?? 'unknown';
    console.log(`  ${g.label}  [${fam}]`);
    for (const v of g.variants) {
      console.log(
        `    ${v.agree ? '✓' : '✗'} ${v.label.padEnd(12)} given=${v.given}` +
          `  出入口=${v.entOk ? '能' : '不能'}` +
          `  预测=${v.predReady === null ? '?' : v.predReady ? 'ready' : 'no'}` +
          `  引擎=${v.ready ? 'ready' : (v.reason || 'no')}` +
          `  接街=${v.attachedNets}/${v.totalNets}` +
          (v.agree ? '' : '  ← 不一致'),
      );
    }
  }
  const allV = pert.flatMap((g) => g.variants);
  const pertAgree = allV.filter((v) => v.agree).length;
  const pertAgree7 = allV.filter((v) => v.agree7).length;
  console.log(`  扰动共 ${allV.length} 次：总判据一致 ${pertAgree}（${pct(pertAgree, allV.length)}），R7 判据一致 ${pertAgree7}（${pct(pertAgree7, allV.length)}）`);

  // 分组结论：这才是扰动实验真正要回答的问题
  //
  // 每一族都有一条**可被否证**的断言。注意 shop/market 与 typed/none 的断言形式不同：
  //   shop/market: ready 次数 == 有给定口的次数（街道怎么改都不动）
  //   parking    : 0 < ready 次数 < 总变体数（街道既能把 no 抬成 ready，也能打回 no）
  //   typed/none : ready 次数 == 总变体数（恒 ready）
  console.log('');
  console.log('  分组结论（每条都是可被否证的断言）：');
  let allGroupsOk = true;
  for (const fam of ['shop', 'market', 'parking', 'typed', 'none'] as EntranceClass[]) {
    const vs = allV.filter((v) => v.family === fam);
    if (!vs.length) continue;
    const yes = vs.filter((v) => v.ready).length;
    const withGiven = vs.filter((v) => v.given > 0).length;
    let ok: boolean;
    let assertion: string;
    if (fam === 'shop' || fam === 'market') {
      ok = yes === withGiven;
      assertion = `ready 次数应等于有给定口的次数（${withGiven}）—— 街道无关`;
    } else if (fam === 'parking') {
      ok = yes > 0 && yes < vs.length;
      assertion = 'ready 次数应介于 0 与总数之间 —— 街道既抬得起也打得到';
    } else {
      ok = yes === vs.length;
      assertion = 'ready 次数应等于总数 —— 恒 ready';
    }
    if (!ok) allGroupsOk = false;
    console.log(`    ${fam.padEnd(8)} ${String(vs.length).padStart(2)} 次变体，ready ${String(yes).padStart(2)} 次  ${ok ? '✓' : '✗'}  ${assertion}`);
  }
  console.log(`    ${allGroupsOk ? ' 全部成立 —— ready.ts 的家族模型没被扰动推翻' : ' ✗ 有断言被推翻，必须回头改 ready.ts'}`);

  const html = render(rows, ranRows, quad, reasons, csharpReady, pert, byFamily, miss.length);
  const out = join(outDir, 'reverse.html');
  writeFileSync(out, html, 'utf8');
  console.log('');
  console.log(`报告 → ${out}`);
}

// ─────────────────────────────── 扰动

interface PerturbVariant {
  label: string;
  /** 契约判定：R7 车行可达。 */
  r7ok: boolean;
  /** 契约判定：R1 有临街。 */
  r1ok: boolean;
  /** 我们的判定：parking 族能否从街上建议出口（只对 parking 家族有意义）。 */
  entOk: boolean;
  entNote: string;
  /** R7 与引擎一致？ */
  agree7: boolean;
  ready: boolean;
  reason: string;
  /** 出入口家族 + 总判据的预测。 */
  family: EntranceClass;
  given: number;
  predReady: boolean | null;
  predBy: string;
  /** 总判据（predictReady）与引擎一致？ */
  agree: boolean;
  counts: { surfaces: number; objects: number };
  /** 接到街的路径条数 / 总路径条数。 */
  attachedNets: number;
  totalNets: number;
  note: string;
}

interface PerturbGroup {
  label: string;
  base: string;
  variants: PerturbVariant[];
}

/** 把街道整体沿"地块形心 → 街中点"方向外移 d 米，等价于"把地块抬离路网"。 */
function pushOutward(raw: RawCase, d: number): RawCase {
  const cx = raw.shape.ring.reduce((s, p) => s + p[0], 0) / raw.shape.ring.length;
  const cy = raw.shape.ring.reduce((s, p) => s + p[1], 0) / raw.shape.ring.length;
  const streets = raw.streets.map((s) => {
    const mx = (s.a[0] + s.b[0]) / 2;
    const my = (s.a[1] + s.b[1]) / 2;
    const dx = mx - cx;
    const dy = my - cy;
    const L = Math.hypot(dx, dy) || 1;
    const ux = (dx / L) * d;
    const uy = (dy / L) * d;
    return {
      ...s,
      a: [s.a[0] + ux, s.a[1] + uy] as [number, number],
      b: [s.b[0] + ux, s.b[1] + uy] as [number, number],
    };
  });
  return { ...raw, streets };
}

function runPerturbations(): PerturbGroup[] {
  const bases = [
    'city24-s8-p42-parking',
    'city24-s8-p42-stripMall',
    'city24-s8-p42-mallPark',
    'city24-s8-p42-marketSquare',
    'city24-s8-p42-shopsPark',
    'city24-s8-p42-park',
    'city24-s8-p42-campground',
    'city24-s8-p42-dogPark',
  ].filter((n) => existsSync(join(caseDir, `${n}.json`)));

  const groups: PerturbGroup[] = [];

  for (const baseName of bases) {
    const raw = JSON.parse(readFileSync(join(caseDir, `${baseName}.json`), 'utf8')) as RawCase;
    const minW = Math.min(...raw.streets.map((s) => s.width));

    // 必须**去掉给定出入口**，否则商场/市场族靠烘焙工具硬塞的那个口就 ready 了，
    // 根本检验不到"街道 → 出入口"这条链路（实测：带 markers 的 case 全变园路也照样 ready）。
    const bare: RawCase = { ...raw, markers: [] };
    const shift = minW / 2 + 6;

    const variants: { label: string; raw: RawCase; note: string }[] = [
      { label: '原样·带给定口', raw, note: '烘焙时的原样（含工具塞的出入口）' },
      { label: '原样·无给定口', raw: bare, note: '去掉给定出入口，让引擎自己建议' },
      {
        label: '外移·无给定口',
        raw: pushOutward(bare, shift),
        note: `全部街外移 ${shift.toFixed(1)} m（gap ≈ ${shift.toFixed(1)} ≤ 15，理论上仍可出入口）`,
      },
      {
        label: '园路·无给定口',
        raw: { ...bare, streets: bare.streets.map((s) => ({ ...s, category: 'pathway' as const })) },
        note: '街在原位但全改成园路（没有车行道可打射线）',
      },
      {
        label: '删最长街·无给定口',
        raw: (() => {
          let bi = 0;
          let bl = -1;
          bare.streets.forEach((s, i) => {
            const L = Math.hypot(s.b[0] - s.a[0], s.b[1] - s.a[1]);
            if (L > bl) {
              bl = L;
              bi = i;
            }
          });
          return { ...bare, streets: bare.streets.filter((_, i) => i !== bi) };
        })(),
        note: '去掉最长的一条街（通常正对主立面）',
      },
      {
        label: '无街·无给定口',
        raw: { ...bare, streets: [] },
        note: '删光所有街 —— 连建议的原料都不给',
      },
    ];

    const out: PerturbVariant[] = [];
    for (const v of variants) {
      const { input, ring2d: vRing, nets: vNets } = toCase(v.raw);
      const rep = validateCase(input);
      const r7ok = rep.rules.find((x) => x.id === 'R7')?.ok ?? false;
      const r1ok = rep.rules.find((x) => x.id === 'R1')?.ok ?? false;
      const family = entranceClass(v.raw.generator);
      const ent = family === 'parking' ? entranceReachable(vRing, vNets) : null;
      const given = v.raw.markers?.length ?? 0;
      const pred = predictReady({ generator: v.raw.generator, givenMarkers: given, suggestOk: ent?.reachable ?? false });

      const p = join(runDir, `${baseName}.${v.label}.json`);
      const casePath = join(runDir, `${baseName}.${v.label}.case.json`);
      writeFileSync(casePath, JSON.stringify(v.raw, null, 1), 'utf8');

      let ready = false;
      let reason = '';
      let counts = { surfaces: 0, objects: 0 };
      let attachedNets = 0;
      let totalNets = 0;
      try {
        execFileSync('dotnet', [dll, '--emit', casePath, p], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
        const plan = JSON.parse(readFileSync(p, 'utf8')) as RawPlan;
        ready = plan.ready;
        reason = reasonOf(plan.readyKey ?? '');
        counts = { surfaces: plan.surfaces?.length ?? 0, objects: plan.objects?.length ?? 0 };
        const nl = plan.nets ?? [];
        totalNets = nl.length;
        attachedNets = nl.filter((n) => (n.startAttach ?? -1) >= 0 || (n.endAttach ?? -1) >= 0).length;
      } catch (e) {
        reason = `emit 失败`;
      }

      out.push({
        label: v.label,
        r7ok,
        r1ok,
        entOk: ent?.reachable ?? false,
        entNote: ent?.note ?? '',
        agree: pred.ready === ready,
        agree7: r7ok === ready,
        ready,
        reason,
        family,
        given,
        predReady: pred.ready,
        predBy: pred.by,
        counts,
        attachedNets,
        totalNets,
        note: v.note,
      });
    }
    groups.push({ label: baseName, base: baseName, variants: out });
  }
  return groups;
}

// ─────────────────────────────── 报告

function render(
  rows: Row[],
  ranRows: Row[],
  quad: { a: number; b: number; c: number; d: number },
  reasons: Map<string, number>,
  csharpReady: number,
  pert: PerturbGroup[],
  byFamily: Map<EntranceClass, Row[]>,
  missCount: number,
): string {
  const css = `
:root{--bg:#f6f5f1;--fg:#1d1c1a;--mut:#6b6862;--line:#dcd8ce;--card:#fff;
--ok:#2f7d54;--bad:#b3402f;--warn:#a5762a;--acc:#3f5f86}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.7 "Microsoft YaHei","PingFang SC",system-ui,sans-serif;padding:28px 32px 80px}
h1{font-size:22px;margin:0 0 4px}
h2{font-size:17px;margin:32px 0 10px;padding-bottom:6px;border-bottom:2px solid var(--line)}
.sub{color:var(--mut);font-size:13px;margin-bottom:20px}
.cards{display:flex;gap:12px;flex-wrap:wrap;margin:14px 0 6px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 16px;min-width:130px}
.card .v{font-size:24px;font-weight:700;line-height:1.2}
.card .k{color:var(--mut);font-size:12px}
table{border-collapse:collapse;width:100%;background:var(--card);font-size:13px;margin-top:8px}
table.auto{width:auto}
th,td{border:1px solid var(--line);padding:5px 8px;text-align:left;vertical-align:top}
th{background:#eceae3;font-weight:600;white-space:nowrap}
td.n{text-align:right;font-variant-numeric:tabular-nums}
tr.fail td{background:#fdf3f1}
.ok{color:var(--ok);font-weight:600}
.bad{color:var(--bad);font-weight:600}
.warn{color:var(--warn)}
.mut{color:var(--mut)}
code{background:#eceae3;padding:0 3px;border-radius:3px;font-size:12px}
.matrix{border-collapse:separate;border-spacing:4px;width:auto}
.matrix td{border:1px solid var(--line);border-radius:6px;padding:14px 20px;text-align:center;background:var(--card)}
.matrix td.h{background:#eceae3;font-weight:600;text-align:center;border:none}
.matrix .big{font-size:22px;font-weight:700;display:block}
.matrix .good{background:#eef6f0}
.matrix .badc{background:#fbeeeb}
`;

  const good = quad.a + quad.d;
  const judged = quad.a + quad.b + quad.c + quad.d;
  const total = ranRows.length;

  const matrix = `
<table class="matrix">
<tr><td class="h"></td><td class="h">引擎 <span class="ok">收下</span></td><td class="h">引擎 <span class="bad">拒收</span></td></tr>
<tr><td class="h">我们预测<br><span class="ok">ready</span></td>
  <td class="good"><span class="big ok">${quad.a}</span>一致</td>
  <td class="badc"><span class="big bad">${quad.b}</span>错报</td></tr>
<tr><td class="h">我们预测<br><span class="bad">not ready</span></td>
  <td class="badc"><span class="big bad">${quad.c}</span>错报</td>
  <td class="good"><span class="big ok">${quad.d}</span>一致</td></tr>
</table>`;

  const familyRows = [...byFamily.entries()]
    .sort((a, b) => b[1].length - a[1].length)
    .map(([f, rs]) => {
      const y = rs.filter((r) => r.ready).length;
      const street = f === 'parking' || f === 'typed';
      const gens = [...new Set(rs.map((r) => r.generator))];
      return `<tr><td><code>${f}</code></td><td class="n">${rs.length}</td><td class="n">${y}</td>
<td>${street ? '<span class="ok">会</span>' : '<span class="bad">不会</span>'}</td>
<td class="mut">${gens.slice(0, 6).join('、')}${gens.length > 6 ? ` 等 ${gens.length} 个` : ''}</td></tr>`;
    })
    .join('');

  const reasonRows = [...reasons.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([k, v]) => `<tr><td><code>${k || '(空)'}</code></td><td>${REASON_CN[k] ?? '<span class="mut">待补中文</span>'}</td><td class="n">${v}</td></tr>`)
    .join('');

  const noReady = ranRows.filter((r) => !r.ready);
  const yesReady = ranRows.filter((r) => r.ready);
  const attNo = noReady.reduce((s, r) => s + r.attachedNets, 0);
  const attYes = yesReady.reduce((s, r) => s + r.attachedNets, 0);

  const detail = rows
    .map((r) => {
      const agree = r.ran && r.predReady !== null ? r.predReady === r.ready : null;
      return `<tr class="${agree === false ? 'fail' : ''}">
<td>${r.name}</td><td>${r.generator}</td><td><code>${r.family}</code></td>
<td class="n">${r.areaM2.toFixed(0)}</td>
<td class="n">${r.given}/${r.auto}</td>
<td>${r.predReady === null ? '<span class="mut">—</span>' : r.predReady ? '<span class="ok">ready</span>' : '<span class="bad">no</span>'}</td>
<td>${r.ran ? (r.ready ? '<span class="ok">ready</span>' : `<span class="bad">${r.reason || 'no'}</span>`) : `<span class="mut">${r.err ?? '未跑'}</span>`}</td>
<td class="n">${r.ran ? `${r.counts.surfaces}/${r.counts.nets}/${r.counts.objects}` : '—'}</td>
<td class="n">${r.ran ? `${r.attachedNets}/${r.totalNets}` : '—'}</td>
<td>${agree === null ? '<span class="mut">—</span>' : agree ? '<span class="ok">✓</span>' : '<span class="bad">✗</span>'}</td></tr>`;
    })
    .join('');

  return `<!doctype html><html lang="zh-CN"><meta charset="utf-8">
<title>反向验证 · 能否预测引擎的 ready</title><style>${css}</style>
<h1>反向验证 · 我们能不能提前说出引擎会怎么判 <code>ready</code></h1>
<div class="sub">对 54 份真实烘焙输入各自真跑一遍 <code>dotnet --emit</code>，把引擎自己的 <code>ready / readyKey</code>
与我们的预测逐份对照。<br>
这一关和前几关的区别：前面验的是"我们算得像不像"，这里验的是"<b>我们能不能提前说出引擎会怎么判</b>"。</div>

<div class="cards">
  <div class="card"><div class="v">${total}</div><div class="k">真跑成功</div></div>
  <div class="card"><div class="v ok">${csharpReady}</div><div class="k">引擎 ready=yes</div></div>
  <div class="card"><div class="v bad">${total - csharpReady}</div><div class="k">引擎 ready=no</div></div>
  <div class="card"><div class="v ${good === judged ? 'ok' : 'warn'}">${good}/${judged}</div><div class="k">预测一致</div></div>
</div>

<h2>一 · <code>ready</code> 到底由什么决定</h2>
<p>这一节是整个验证的地基。<b>前一轮在这里搞错了</b>：把「出入口可达」当成 ready 的判据去套所有生成器，
结果在商场/市场族上全线假阳。逐行核对 <code>Emit.cs:95-111</code> 后，真实的因果链只有三步：</p>
<pre style="background:#eceae3;padding:10px 14px;border-radius:6px;overflow-x:auto;font-size:12.5px">given  = input.Markers.Count                  // 烘焙工具塞的 / 玩家点的
sug    = gen.SuggestMarkers(input)            // ① 建议 —— 只有这一步碰街道
if (sug非空 &amp;&amp; given==0) input.Markers += sug // ② 建议被写回成真 marker
ready  = gen.CheckReady(input)                // ③ 只数 marker，从不看街道</pre>
<p>所以 <code>ready ⇔ CheckReady(given&gt;0 ? given : suggest)</code>。而 <code>suggest</code> 是否为空，
由<b>两个互相独立的开关</b>决定（<code>SuggestMarkers</code> 与 <code>CheckReady</code> 是两套平行分家，互不通用）：</p>

<table><tr><th>家族</th><th>建议器</th><th>是否碰街道</th><th>CheckReady 判据</th></tr>
<tr><td><code>shop</code></td><td>恒空 <span class="mut">CommercialLayer.cs:41</span></td>
  <td><span class="bad">不会</span></td><td>车行口：two&gt;0 或 in&gt;0∧out&gt;0 <span class="mut">MallEntrances.cs:54</span></td></tr>
<tr><td><code>market</code></td><td>恒空 <span class="mut">MarketSquareLayer.cs:49</span></td>
  <td><span class="bad">不会</span></td><td>任意一个口即可 <span class="mut">MarketEntrances.cs:55</span></td></tr>
<tr><td><code>parking</code></td><td><b>EntranceSuggest</b>：40 m 外射线</td>
  <td><span class="ok">会</span></td><td>车行口 <span class="mut">ParkingLayer.cs:235</span></td></tr>
<tr><td><code>typed</code></td><td>Entrances.Suggest：FrontageReach 前沿</td>
  <td><span class="ok">会</span>（另一套判据）</td><td>任一 typed kind <span class="mut">Entrances.cs:340</span></td></tr>
<tr><td><code>none</code></td><td>空，或非空但不影响</td>
  <td>不适用</td><td>恒 true <span class="mut">LayerBase.cs:428</span></td></tr>
</table>

<h3 style="font-size:14px;margin:20px 0 4px">这 ${total} 份夹具落在哪些家族</h3>
<table><tr><th>家族</th><th>份数</th><th>ready=yes</th><th>街道能影响 ready？</th><th>生成器</th></tr>${familyRows}</table>
<p class="mut"><b>结论（docs/D 的 D-022）：街道契约能影响 <code>ready</code> 的只有 parking 一个家族。</b>
商场（stripMall/mallPark/mallDistrict）和市集（marketSquare）的 ready 与街道零因果——
街道摆得再好也抬不成 ready，删光了也不会更差。它们只认玩家或烘焙工具给的口。</p>

<h2>二 · 预测 vs 引擎（四格表）</h2>
${matrix}
<p class="mut">${missCount === 0 ? '零错报。' : `<b>${missCount} 份错报</b>，见 §六 的红行。`}
判据完全来自上表：有给定口就 ready；没有就看该家族的建议器能不能给出一个。
不掺面积下限、'放不下'这类引擎侧原因——那些是"建议器给不出口"的后果，不是独立判据。</p>

<h2>三 · 引擎的拒收原因分布</h2>
<table class="auto"><tr><th>readyKey 的 Reason</th><th>中文</th><th>份数</th></tr>${reasonRows}</table>

<h2>四 · 街道真正的作用点不是 <code>ready</code></h2>
<p>既然 ready 只数 marker，那街道规则到底管什么？在 <code>LayerBase.cs:521-531</code>：</p>
<pre style="background:#eceae3;padding:10px 14px;border-radius:6px;overflow-x:auto;font-size:12.5px">if (!CheckReady(input).Ready) TrimOutlineEnds(plan, region, unplacedInset);
else if (!CheckReady(带空markers).Ready) { /* 只保留 marker 附近 5 m 内的接轮廓 */ }</pre>
<p><b><code>ready=no</code> 不是失败，是「路径不许接到街上」的降级开关。</b>布局照样生成，只是内部路网封在轮廓内。
这就是之前观察到的"静默降级"的精确机制。量出来的结果：</p>
<div class="cards">
  <div class="card"><div class="v ${attNo === 0 ? 'ok' : 'bad'}">${attNo}</div><div class="k">ready=no 的 ${noReady.length} 份，接到街的路径条数</div></div>
  <div class="card"><div class="v">${attYes}</div><div class="k">ready=yes 的 ${yesReady.length} 份，接到街的路径条数</div></div>
</div>
<p class="mut">左卡片恒为 0，那正是 <code>TrimOutlineEnds</code> 在起作用。但别把它当成"我们算对了"的证据——
这里数的是<b>已有 <code>startAttach/endAttach</code> 的路径条数</b>，与 <code>ready</code> 是两条独立的线：
右卡片的 16 条远小于 53 份，正因为<b>建议出来的口 <code>AttachId</code> 恒为 -1</b>
（<code>Entrances.cs:371</code> 的 <code>Suggested = true</code>），只有玩家或烘焙工具给的口才真接街。</p>

<h2>五 · 扰动实验（看断言本身成不成立）</h2>
<p class="mut">54 份真实数据是 <b>53 收 1 拒</b>，样本极度不平衡，一致率高说明不了区分力。
但这里扰动实验的目的已经变了：不是"证明判据对"，而是检验<b>断言本身</b>——
每一族都有一条<b>可被否证</b>的断言：</p>
<table class="auto"><tr><th>家族</th><th>断言</th></tr>
<tr><td><code>shop</code> / <code>market</code></td><td>ready 次数应<b>正好等于</b>有给定口的次数 —— 街道怎么改都不动</td></tr>
<tr><td><code>parking</code></td><td>ready 次数应<b>介于 0 与总数之间</b> —— 街道既抬得起也打得到</td></tr>
<tr><td><code>typed</code> / <code>none</code></td><td>ready 次数应<b>等于总数</b> —— 恒 ready</td></tr>
</table>
<p class="mut">最后一个变体「无街·无给定口」是最狠的一刀：把街道<b>全删光</b>。公园族应当照旧 ready，
因为 <code>Entrances.Suggest</code> 在候选为空时会兜底"最长边的中点"（<code>Entrances.cs:239-259</code>）——
这正是 <code>ready.ts</code> 里 <code>typed</code> 族那条结论的结构性依据，而不是经验。</p>
${pert
  .map((g) => {
    const fam = g.variants[0]?.family ?? 'unknown';
    const rows2 = g.variants
      .map(
        (v) => `<tr class="${v.agree ? '' : 'fail'}">
<td>${v.label}</td><td class="mut">${v.note}</td>
<td class="n">${v.given}</td>
<td>${v.predReady === null ? '<span class="mut">—</span>' : v.predReady ? '<span class="ok">ready</span>' : '<span class="bad">no</span>'}</td>
<td>${v.ready ? '<span class="ok">ready</span>' : `<span class="bad">${v.reason || 'no'}</span>`}</td>
<td class="n">${v.attachedNets}/${v.totalNets}</td>
<td class="n">${v.counts.surfaces}/${v.counts.objects}</td>
<td>${v.agree ? '<span class="ok">✓</span>' : '<span class="bad">✗</span>'}</td></tr>`,
      )
      .join('');
    return `<h3 style="font-size:14px;margin:18px 0 4px">${g.label} <code>${fam}</code></h3><table><tr><th>变体</th><th>改了什么</th><th>给定口</th><th>预测</th><th>引擎</th><th>接街</th><th>面/物</th><th></th></tr>${rows2}</table>`;
  })
  .join('')}

<h2>六 · 逐份对照</h2>
<table><tr><th>地块</th><th>生成器</th><th>家族</th><th>面积 m²</th><th>给/补口</th><th>预测</th><th>引擎判定</th><th>面/渠/物</th><th>接街</th><th></th></tr>${detail}</table>
<p class="mut">"给/补口" = <code>plan.markers.given / auto</code>，即输入里给的口数与建议器补的口数。
"面/渠/物" = 引擎输出的 surfaces / nets / objects 数量。"接街" = 有 <code>startAttach/endAttach</code> 的路径条数 / 总条数。</p>
</html>`;
}

main();
