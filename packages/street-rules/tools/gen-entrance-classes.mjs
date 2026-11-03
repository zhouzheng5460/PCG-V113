// 从 C# 反射结果生成 "出入口家族表"。
//
// 为什么不再手抄：ready.ts 原来那张 32 行表是人工对照源码抄的，抄完还剩 98 个 unknown，
// 而且已经抄错过一次（plaza 被归成 typed，实际 CheckReady 恒 true —— docs/D 的 D-024）。
// 手抄 130 行必然会漂移；反射出来的只有 12 种组合，规则一写就全覆盖。
//
// 输入：tools/batch/generators.json（由 driver 的 --generators 产出，已入库，构建不需要 dotnet）
// 输出：packages/street-rules/src/entrance-classes.generated.ts
//
// 跑法：node packages/street-rules/tools/gen-entrance-classes.mjs
//      加 --refresh 会先调 dotnet 重新反射一遍（需要本机有 driver 构建）

import { readFileSync, writeFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { dirname, join, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../../..');
const srcJson = join(root, 'tools/batch/generators.json');
const outTs = join(here, '../src/entrance-classes.generated.ts');
const vendorDir = join(root, 'vendor/placemaker');

// ───────────────────────────────── 归类规则
//
// 每条规则的依据都是"CheckReady/SuggestMarkers 最近一次被谁声明"+ 两个 protected
// virtual 开关在该实例上的值。规则少是因为这套继承结构本身就收敛：
//   LayerBase           CheckReady => new Readiness()  恒 true
//   CommercialLayer     UsesEntrances ? ShopEntrances.Ready : base
//   LeisureLayer        TypedEntrances ? RequireTyped(Leisure) : Ready=true
//   MarketSquareLayer   MarketEntrances.Ready
//   ParkingLayer        ParkingEntrances.Ready（唯一走 EntranceSuggest 的族）
//   ParkLayer           RequireTyped(Park)
//   CampgroundLayer     RequireTyped(Leisure)
//   PlazaLayer          Ready = true
//   RoadsLayer          看路网图案 + 地形
//   LayerAlias          委托给 canonical id
//
// 规则没覆盖到的声明点**不猜**，留 unknown 并打印出来 —— 出现新的继承基类时必须被人看见。

/** @returns {'shop'|'market'|'parking'|'typed'|'none'|'roads'|'unknown'|null} null = 别名，待穿透 */
function familyOf(r) {
  switch (r.checkReadyBy) {
    case 'LayerAlias':
      return null;
    case 'ParkingLayer':
      return 'parking';
    case 'MarketSquareLayer':
      return 'market';
    case 'CommercialLayer':
      // UsesEntrances=true → ShopEntrances.Ready（要车行口）；false → 落到 base 恒 true
      return r.usesEntrances === true ? 'shop' : 'none';
    case 'LeisureLayer':
      // TypedEntrances=true → RequireTyped(Leisure)；false → Ready=true（但建议仍走街道）
      return r.typedEntrances === true ? 'typed' : 'none';
    case 'ParkLayer':
    case 'CampgroundLayer':
      return 'typed';
    case 'PlazaLayer':
      // CheckReady => new Readiness { Ready = true }，恒 true；建议走 Entrances.Suggest
      return 'none';
    case 'RoadsLayer':
      return 'roads';
    case 'LayerBase':
    case 'EdgeZoningAddOn':
      return 'none';
    default:
      return 'unknown';
  }
}

/** 建议器会不会产出非空 marker（决定 auto 计数，不等于影响 ready）。 */
function suggestsOf(r) {
  switch (r.suggestBy) {
    case 'LayerAlias':
      return null;
    // 这些都直接返回空表
    case 'LayerBase':
    case 'CommercialLayer': // UsesEntrances ? 空 : base(空) —— 两条分支都是空
    case 'MarketSquareLayer':
    case 'RoadsLayer':
    case 'EdgeZoningAddOn':
      return false;
    default:
      // LeisureLayer / PlazaLayer / ParkLayer / CampgroundLayer / ParkingLayer 都可能非空
      return true;
  }
}

// ───────────────────────────────── 源码定位（file:line 注释）
//
// family 是反射确定的，但人要能顺着注释查回去。反射拿不到行号，只能在源码里找声明行。

const csIndex = new Map(); // TypeName -> 文件绝对路径（同名多个时取第一个并标 ?）
function indexSources(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    const s = statSync(p);
    if (s.isDirectory()) {
      indexSources(p);
    } else if (name.endsWith('.cs')) {
      const t = basename(name, '.cs');
      if (!csIndex.has(t)) csIndex.set(t, p);
      else csIndex.set(t, '?'); // 重名，放弃定位
    }
  }
}

/** 在该类型文件里找方法/属性的声明行号。 */
function lineOf(typeName, needle) {
  const p = csIndex.get(typeName);
  if (!p || p === '?') return '';
  const lines = readFileSync(p, 'utf8').split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].includes(needle) && !lines[i].trimStart().startsWith('//')) return i + 1;
  }
  return '';
}

function cite(typeName, method, prop) {
  const file = basename(csIndex.get(typeName) ?? '', '.cs');
  if (!file || !csIndex.get(typeName) || csIndex.get(typeName) === '?') return '';
  const a = lineOf(typeName, method);
  const b = prop ? lineOf(typeName, prop) : '';
  const parts = [a, b].filter(Boolean).map((n) => ':' + n);
  return file ? `${file}.cs${parts.join(' ')}` : '';
}

// ───────────────────────────────── 主流程

function refresh() {
  const exe = join(root, 'tools/oracle/driver/bin/Debug/net6.0/PlacemakerDriver.exe');
  if (!existsSync(exe)) {
    console.error(`找不到 ${exe}，先跑 dotnet build tools/oracle/driver/Driver.csproj`);
    process.exit(2);
  }
  const r = spawnSync(exe, ['--generators', srcJson], { encoding: 'utf8', cwd: root });
  if (r.status !== 0) {
    console.error(r.stdout ?? '', r.stderr ?? '');
    process.exit(r.status ?? 1);
  }
  process.stdout.write(r.stdout ?? '');
}

function main() {
  if (process.argv.includes('--refresh')) refresh();
  if (!existsSync(srcJson)) {
    console.error(`缺 ${srcJson}。跑：node packages/street-rules/tools/gen-entrance-classes.mjs --refresh`);
    process.exit(2);
  }
  indexSources(vendorDir);

  const doc = JSON.parse(readFileSync(srcJson, 'utf8'));
  const rows = doc.generators;
  const byId = new Map(rows.map((r) => [r.id, r]));

  const out = [];       // { id, family, suggests, by, cite, note }
  const unknown = [];
  const unresolved = [];

  for (const r of rows) {
    let fam = familyOf(r);
    let sug = suggestsOf(r);
    let target = r;

    if (fam === null) {
      // 别名：穿透到 canonical，family/suggests 都跟随它
      const c = byId.get(r.canonicalId);
      if (!c) {
        unknown.push({ id: r.id, why: `别名指向不存在的 id: ${r.canonicalId}` });
        fam = 'unknown';
        sug = false;
      } else {
        fam = familyOf(c);
        sug = suggestsOf(c);
        target = c;
      }
    }
    if (fam === 'unknown') {
      unknown.push({ id: r.id, why: `未覆盖的 CheckReady 声明点: ${r.checkReadyBy}` });
    }

    // 引用：别名要同时标注委托关系
    const citeText = r.isAlias
      ? `别名 → ${r.canonicalId}（${cite(target.checkReadyBy, 'CheckReady') || target.checkReadyBy}）`
      : cite(r.checkReadyBy, 'CheckReady', 'UsesEntrances') || r.checkReadyBy;

    out.push({
      id: r.id,
      family: fam,
      suggests: sug,
      order: r.order,
      hidden: r.hidden,
      group: r.group,
      note: citeText,
    });
  }

  // 覆盖度自检：夹具里出现过的生成器必须在表里，否则分类再准也没用
  const caseDir = join(root, 'tools/batch/cases');
  const used = new Set();
  if (existsSync(caseDir)) {
    for (const f of readdirSync(caseDir)) {
      if (!f.endsWith('.json')) continue;
      try {
        const d = JSON.parse(readFileSync(join(caseDir, f), 'utf8'));
        if (d.generator) used.add(d.generator);
      } catch { /* 坏夹具由别的脚本报 */ }
    }
  }
  for (const g of used) if (!byId.has(g)) unresolved.push(g);

  // ───────────────────────────── 写文件
  const stat = (f) => out.filter((r) => r.family === f).length;
  const header = `// 由 packages/street-rules/tools/gen-entrance-classes.mjs 自动生成，不要手改。
//
// 数据来源：${doc.protocol}（driver 的 --generators 反射 GeneratorRegistry.All）
// ${doc.count} 个生成器，${new Set(out.map((r) => r.family)).size} 个家族。
//
// 分布：none ${stat('none')}｜typed ${stat('typed')}｜parking ${stat('parking')}｜
//       shop ${stat('shop')}｜market ${stat('market')}｜roads ${stat('roads')}｜unknown ${stat('unknown')}
//
// 为什么要生成：手抄 130 行会漂移，而且已经漂过一次（plaza 被误归为 typed，
// 实际 PlazaLayer.CheckReady 恒 true —— docs/D 的 D-024）。归类只依赖"虚方法最近声明点
// + 两个 protected virtual 开关的实例值"，反射能全部拿到，人抄不出来。
//
// 每行的注释是那个声明的**源码位置**（file:line），用来顺藤摸瓜回读实现。

/** 出入口家族：决定 CheckReady 用什么判据。 */
export type GeneratedFamily =
  | 'shop'      // ShopEntrances.Ready：要有车行口；建议恒空
  | 'market'    // MarketEntrances.Ready：任一口即可；建议恒空
  | 'parking'   // ParkingEntrances.Ready；建议走 EntranceSuggest（碰街道）
  | 'typed'     // RequireTyped；建议走 Entrances.Suggest/SuggestTyped（碰街道）
  | 'none'      // CheckReady 恒 true
  | 'roads'     // 看路网图案 + 地形，与街道契约无关
  | 'unknown';  // 未覆盖，不猜

/** 生成器 id → 家族。 */
export const ENTRANCE_TABLE: Record<string, GeneratedFamily> = {
`;

  const body = out
    .map((r) => {
      const flags = [r.hidden ? 'hidden' : '', r.group ? 'group=' + r.group : ''].filter(Boolean).join(', ');
      const tail = [r.note, flags].filter(Boolean).join('  ');
      return `  ${JSON.stringify(r.id)}: ${JSON.stringify(r.family).replace(/"/g, "'")},${tail ? ' // ' + tail : ''}`;
    })
    .join('\n');

  const footer = `
};

/** 生成器 id → 建议器是否可能产出非空 marker（决定 auto 计数，不等于影响 ready）。 */
export const SUGGESTS_TABLE: Record<string, boolean> = {
${out
  .filter((r) => r.suggests)
  .map((r) => `  ${JSON.stringify(r.id)}: true,`)
  .join('\n')}
};

/** 表里一共有多少个生成器（用于和 driver 的 count 对账）。 */
export const GENERATOR_COUNT = ${out.length};
`;

  writeFileSync(outTs, header + body + footer, 'utf8');

  console.log(`家族表 → packages/street-rules/src/entrance-classes.generated.ts`);
  console.log(
    `  ${out.length} 个生成器：` +
      ['none', 'typed', 'parking', 'shop', 'market', 'roads', 'unknown']
        .map((f) => `${f} ${stat(f)}`)
        .join('｜'),
  );
  const sugCount = out.filter((r) => r.suggests).length;
  console.log(`  会建议 marker 的 ${sugCount} 个（其中 ready 恒 true 的 ${out.filter((r) => r.suggests && (r.family === 'none')).length} 个：街上有路只会多出 marker，不改变 ready）`);
  if (unknown.length) {
    console.error(`  未归类 ${unknown.length} 个，必须处理：`);
    for (const u of unknown) console.error(`    ${u.id}: ${u.why}`);
    process.exitCode = 1;
  }
  if (unresolved.length) {
    console.error(`  夹具里用到但反射里没有的 id：${unresolved.join(', ')}`);
    process.exitCode = 1;
  }
}

main();
