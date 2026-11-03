// ready.ts — Placemaker 的 `ready` 判定模型。
//
// 为什么单独一个文件：`ready` 不是街道规则，但街道契约能不能**预测**它，是 P1 验收门的
// 最后一条。要把这条验清楚，必须先写清楚 ready 到底由什么决定，否则就会像前一轮那样
// 拿"出入口可达"当判据去套所有生成器，在商场/市场族上全线假阳。
//
// ── ready 的真实因果链（Emit.cs:95-111，逐行核对过）──────────────────────
//
//   input   = BuildInput(...)
//   given   = input.Markers.Count                      // 烘焙工具塞的 / 玩家点的
//   sug     = gen.SuggestMarkers(input)                // ① 建议，家族相关
//   if (sug非空 && input.Markers空) input.Markers += sug   // ② 建议被写回成真 marker
//   ready   = gen.CheckReady(input)                     // ③ 只数 marker，从不看街道
//
// 三步里只有 ① 碰街道。所以：
//
//   ready ⇔ CheckReady( given>0 ? given : suggest )
//
// 而 `suggest` 是否为空，由**两个互相独立的开关**决定，这是最容易搞错的地方：
// （下表 130 个生成器的家族分布来自反射，见文件末的"家族表"一节）
//
//   | 家族            | 数量 | SuggestMarkers                        | 是否碰街道 |
//   |-----------------|-----|---------------------------------------|-----------|
//   | shop            |   3 | 恒空（CommercialLayer.cs:41）           | 否        |
//   | market          |   1 | 恒空（MarketSquareLayer.cs:49）         | 否        |
//   | parking         |   1 | EntranceSuggest：40 m 外射线           | **是**    |
//   | typed（park族） |  27 | Entrances.Suggest：FrontageReach 前沿  | **是**（另一套判据）|
//   | none            |  97 | 空，或非空但 CheckReady 恒 true         | 不适用    |
//   | roads           |   1 | 恒空（RoadsLayer.cs:138）              | 否        |
//
// ── 为什么 typed 族恒 ready（结构性，不是经验）──────────────────────────
//
// `Entrances.Suggest` 在 `candidates.Count == 0` 时会兜底"最长边的中点"，再补 15 个
// 均匀点（Entrances.cs:239-259）。所以只要 region 合法（ring ≥3 点）且 wanted ≥1，
// 返回列表**一定非空**；而 `WantedByArea` 最小给 1（:113）。`SuggestTyped` 的第一条
// 就是 `Suggest(..., Pedestrian)`，kind 必然落在 `Park`/`Leisure` 表里（:40-42），
// 于是 `RequireTyped` 一定通过。**街道在这里连"能不能 ready"都影响不了。**
//
// 这条推断在扰动实验里被正面验证过：park/campground/dogPark 三个地块各 6 种变体
// （含"删光所有街"），18/18 全部 ready。
//
// `CheckReady` 一侧同样分家（五套判据，互不通用）：
//
//   | 家族    | 判据                                          | 源码 |
//   |---------|-----------------------------------------------|------|
//   | shop    | 车行 kind：two>0 或 (in>0 且 out>0)            | MallEntrances.cs:54 |
//   | market  | 任一 marker 即可（一个给人走的门就算成市集）     | MarketEntrances.cs:55 |
//   | parking | 同 shop，kind 表不同                           | ParkingLayer.cs:235 |
//   | typed   | 任一匹配 kind，legacy "entrance" 也算           | Entrances.cs:340 |
//   | none    | 恒 true                                        | LayerBase.cs:428 |
//   | roads   | 图案 × 地形（等高线图案要求地形起伏）           | RoadsLayer.cs:115 |
//
// ── 由此得到的结论（docs/D 的 D-022）────────────────────────────────────
//
// **ready 会被街道改变的生成器，只有 parking 一个家族。**
// 商场（stripMall/mallPark/mallDistrict）、市集（marketSquare）、路网（roads）的
// ready 与街道零因果：街道摆得再好也变不成 ready，删光了也不会更差。
// typed 族虽然也从街道取建议，但它有最长边兜底（下一节），街道**改变不了**结果。
//
// 来源标注（docs/D §4 规则 6：任何"必是/恒"的断言都要写清出处）：
//   `shop`/`market`/`parking`/`typed`/`none`/`roads` 的归类**不再手写** —— 由
//   packages/street-rules/tools/gen-entrance-classes.mjs 从 driver 的 `--generators`
//   反射结果生成（见 entrance-classes.generated.ts）。反射拿到的是"虚方法最近声明点
//   + 两个 protected virtual 开关的实例值"，130 个生成器全覆盖、unknown 为 0。
//   `none` 与 `typed` 的**实际表现**另外由 54 份真实烘焙输入实测印证
//   （见 packages/demo/src/reverse.ts 的对照表）。
import { ENTRANCE_TABLE, SUGGESTS_TABLE } from "./entrance-classes.generated.js";
// ─────────────────────────────── 家族表
//
// 手写表已经被取代。上一版那张 32 行表有 98 个 unknown，而且抄错过一次：
// plaza 被归成 typed，实际 `PlazaLayer.CheckReady => new Readiness { Ready = true }`
// 恒 true（PlazaLayer.cs:283），街道对它的 ready 零影响（docs/D 的 D-024）。
// 现在的表是生成的，别再往这里加条目 —— 要改归类就改 gen-entrance-classes.mjs 的规则。
/** 取生成器的出入口家族；表里没有的返回 `unknown`（不猜）。 */
export function entranceClass(generator) {
    return ENTRANCE_TABLE[generator] ?? 'unknown';
}
/**
 * 该生成器的建议器会不会产出非空 marker。
 *
 * 和"街道能否影响 ready"是**两件事**，别混：`none` 族里有 14 个会建议 marker
 * （PlazaLayer / LeisureLayer(TypedEntrances=false) 走 `Entrances.Suggest`），
 * 街上有路时它们会多出 marker，但 ready 恒 true —— 观察到的 `auto>0` 就是这条。
 */
export function suggestsMarkers(generator) {
    return SUGGESTS_TABLE[generator] === true;
}
/** 家族是否可能在 ready 上受街道影响（只有这两个家族建议器碰街道）。 */
export function streetCanAffectReady(generator) {
    const c = entranceClass(generator);
    return c === 'parking' || c === 'typed';
}
/**
 * 预测 Placemaker 的 `ready`。
 *
 * 注意这不是"我们的规则是否通过"——它是"引擎会不会收下这块地"。
 * 两者不是一回事：契约全绿的地块仍可能因为家族要求出入口而 ready=no。
 */
export function predictReady(q) {
    const c = entranceClass(q.generator);
    // 有口就轮不到建议器，三个"要求口"的家族此时一律 ready。
    if (c === 'shop' || c === 'market' || c === 'parking') {
        if (q.givenMarkers > 0)
            return { ready: true, by: `已给 ${q.givenMarkers} 个口，建议器不参与` };
    }
    switch (c) {
        case 'shop':
            return { ready: false, by: '商场族：建议恒空，没有给定口就永远 NeedsEntrance（与街道无关）' };
        case 'market':
            return { ready: false, by: '市集族：建议恒空，没有给定口就永远 NeedsEntrance（与街道无关）' };
        case 'parking':
            return q.suggestOk
                ? { ready: true, by: '停车族：EntranceSuggest 能从街上建议出口' }
                : { ready: false, by: '停车族：40 m 内打不到可用的街，建议不出来' };
        case 'typed':
            // 这不是"实测总能给出"的经验，是**结构性必然**：
            //   Suggest() 在 candidates 为空时会兜底"最长边的中点"（Entrances.cs:239-259），
            //   所以只要 region 合法（ring ≥3 点）且 wanted ≥1，返回列表一定非空；
            //   而 WantedByArea 最小给 1（Entrances.cs:113-120），SuggestTyped 的第一条
            //   就是 Suggest(..., Pedestrian)，kind 必然落在 Park/Leisure 里（Entrances.cs:40-42），
            //   于是 RequireTyped 一定通过。
            // 想让 typed 族 ready=no，只有一条路：ring 不合法——那时生成器自己也报 BadShape。
            return { ready: true, by: '公园族：Suggest 有最长边兜底，结构性必然非空 → 必定 ready' };
        case 'none':
            return { ready: true, by: 'CheckReady 恒 true，与出入口无关' };
        case 'roads':
            // 只有"等高线图案 + 地形平坦"这一条会判不 ready（RoadsLayer.cs:115-131），
            // 与街道契约无关，我们这里没有地形数据，不预测。
            return { ready: null, by: '路网族：ready 取决于图案×地形，与街道契约无关，本工具不预测' };
        case 'unknown':
            return { ready: null, by: '生成器未在反射表里，不预测' };
    }
}
