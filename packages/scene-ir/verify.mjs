// scene-ir 的离线验收：不需要浏览器，也不需要 three。
//
// 这一层是"生成规则"与"渲染"的分界，所以它的验收也只能是**数据级**的：
//   · 同一方案两次生成，节点 id 与几何指纹逐字节一致（确定性）
//   · 改一栋楼的层数，**只有一个**节点的指纹变了（增量可判定）
//   · 关一个图层，只少那一层的节点，其余节点 id 一个不少
//   · 这个包里不出现 THREE（保证能被 node 直接跑）
//
// 跑法：node packages/scene-ir/verify.mjs

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');

const { buildSceneIR } = await import('./src/index.ts');
const { geomKey, irKey } = await import('./src/types.ts');

const planDir = join(root, 'tools/batch/plans');
const caseDir = join(root, 'tools/batch/cases');
const fails = [];
const ok = (cond, msg) => {
  if (!cond) fails.push(msg);
};

const ALL = { base: true, surfaces: true, streets: true, zoning: true, massing: true, props: true, markings: true };

function loadPlan(id) {
  const plan = JSON.parse(readFileSync(join(planDir, `${id}.plan.json`), 'utf8'));
  const cs = JSON.parse(readFileSync(join(caseDir, `${id}.json`), 'utf8'));
  return {
    id,
    ring: cs.shape.ring,
    ch: {
      surfaces: plan.surfaces ?? [],
      nets: plan.nets ?? [],
      objects: plan.objects ?? [],
      zoning: plan.zoning ?? [],
    },
  };
}

const files = existsSync(planDir) ? readdirSync(planDir).filter((f) => f.endsWith('.plan.json')) : [];
const ids = files.map((f) => f.replace(/\.plan\.json$/, ''));
if (ids.length < 4) fails.push(`带宗地的方案只有 ${ids.length} 份，预期 ≥4`);

const report = [];

for (const id of ids) {
  const { ring, ch } = loadPlan(id);
  const opts = { toggles: ALL, seedKey: `x:${id}` };

  const a = buildSceneIR(ch, ring, opts);
  const b = buildSceneIR(ch, ring, opts);
  ok(irKey(a) === irKey(b), `${id}: 两次生成指纹不一致（不确定性）`);
  ok(a.nodes.length > 0, `${id}: 0 个节点`);

  // 节点 id 必须唯一 —— 不唯一的话 realizer 的 Map 会互相覆盖，症状是"少画了一块"
  const seen = new Set();
  for (const n of a.nodes) {
    ok(!seen.has(n.id), `${id}: 节点 id 重复 ${n.id}`);
    seen.add(n.id);
  }

  // ── 改一栋楼的层数：只有那一个节点变
  const blds = a.nodes.filter((n) => n.layer === 'massing');
  const target = blds.find((n) => (n.props.fmax ?? 1) > (n.props.fmin ?? 1));
  if (target) {
    // 要改成**和当前不同**的值：stripMall 的楼本来就都在下限上，改到 fmin 是个空操作，
    // 断言会变成"改了却 0 个节点变" —— 那是测试写错，不是增量失效。
    const want = target.props.floors === target.props.fmin ? target.props.fmax : target.props.fmin;
    const raised = buildSceneIR(ch, ring, { ...opts, floors: { [target.id]: want } });
    const before = new Map(a.nodes.map((n) => [n.id, geomKey(n)]));
    const after = new Map(raised.nodes.map((n) => [n.id, geomKey(n)]));
    const changed = [...before].filter(([k, v]) => after.get(k) !== v).map(([k]) => k);
    ok(changed.length === 1 && changed[0] === target.id, `${id}: 改一栋楼却变了 ${changed.length} 个节点（${changed.slice(0, 4)}）`);
    const t2 = raised.nodes.find((n) => n.id === target.id);
    const expH = want * target.props.floorHeight;
    const gotH = t2.y1 - t2.y0;
    ok(Math.abs(gotH - expH) < 1e-9, `${id}: 楼高 ${gotH.toFixed(3)} ≠ 层数×层高 ${expH.toFixed(3)}`);
    ok(t2.props.floors === want, `${id}: 层数没被覆盖（要 ${want}，得 ${t2.props.floors}）`);
    report.push({ id, nodes: a.nodes.length, buildings: blds.length, changed: changed.length, h: +gotH.toFixed(2) });
  } else {
    report.push({ id, nodes: a.nodes.length, buildings: blds.length, changed: 0, h: 0 });
  }

  // ── 关一个图层：只少那一层的节点，其余 id 一个不少
  for (const [layer, label] of [
    ['props', '绿植/家具'],
    ['markings', '标线'],
    ['streets', '街道'],
    ['zoning', '宗地'],
    ['surfaces', '地面'],
  ]) {
    const off = buildSceneIR(ch, ring, { ...opts, toggles: { ...ALL, [layer]: false } });
    const live = new Set(off.nodes.map((n) => n.id));
    const lost = a.nodes.filter((n) => !live.has(n.id));
    const added = off.nodes.filter((n) => !a.nodes.some((m) => m.id === n.id));
    ok(added.length === 0, `${id}: 关${label}却多了 ${added.length} 个节点`);
    ok(
      lost.every((n) => n.layer === layer),
      `${id}: 关${label}却误删了别的层（${lost.filter((n) => n.layer !== layer).map((n) => n.id).slice(0, 3)}）`,
    );
    // 楼块节点是跨层依赖：关宗地会把楼也带走（楼是从宗地切出来的），这是设计如此
    if (layer !== 'zoning' && layer !== 'massing') {
      ok(lost.length > 0 === layer !== 'base', `${id}: 关${label}一个节点都没少`);
    }
  }
}

// ── 包纯洁性：scene-ir 里不许出现 THREE
for (const f of readdirSync(join(here, 'src'))) {
  if (!f.endsWith('.ts')) continue;
  const src = readFileSync(join(here, 'src', f), 'utf8');
  ok(!/\bTHREE\b/.test(src.replace(/\/\/.*$/gm, '')), `scene-ir/src/${f} 里出现了 THREE（这一层必须是纯数据）`);
}

const out = { ok: fails.length === 0, plans: report, fails };
console.log(JSON.stringify(out, null, 2));
process.exitCode = out.ok ? 0 : 1;
