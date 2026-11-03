// TS 生成器 v0：一块地 → 街道 + 宗地 + 楼位 + 建筑。
//
// 为什么现在只能叫 v0：Placemaker 只给**宗地**，不给街道；它默认"街道是玩家画的"。
// 而我们这个编辑器必须自己造街道，否则整条链（出入口接街 → ready → 允许布局）断掉。
// docs/A 把街道规则整理成了 R1–R11 验收条件，v0 是第一个"能满足最低几条"的版本：
//   · 街道是一条贯通地块的直路（有起点有终点，不悬空）
//   · 街道两侧留出人行道，车行道宽度由路型决定
//   · 宗地**沿街**切分，临街面朝街（这是 Placemaker 判断出入口的依据）
//   · 楼位落在宗地内，尺寸走 KIND_SPECS（面宽/进深/层数区间）
//
// v0 明确没做的事（写在面板上，不假装）：
//   · 没有 ready 判定（那是 C# 的 CheckReady，要逐家族移植，仍在 G5）
//   · 没有多街道交叉/环路（一条脊街，够小地块用）
//   · 没有地形高差（全平）
//
// 参数怎么用（docs/G §3 的映射表，逐条落实，不许静默忽略）：
//   density   → 基底面积上限，超了就不再放楼
//   far       → 总建筑面积上限，超了就把楼层压到下限
//   heightRange → 夹取层数（覆盖 KIND_SPECS 的默认区间）
//   seed      → 街道偏移与楼位层数的唯一随机源
import { ZONE_PREFAB } from "../../plan-ir/src/types.js";
import { KIND_SPECS, cutZoning } from "../../slots/src/cut.js";
import { areaPaths, commonDefaults, } from "./model.js";
import { boundsOfPaths, ringArea } from "./polygon.js";
import { dist, norm, perp, sub } from "./vec.js";
/** 退线：楼不贴着地块边线。真实城市都有，缺了画面一眼假。 */
const SETBACK = 4;
/** 脊街两侧留的余量（人行道 + 楼前场地）。 */
const STREET_MARGIN = 6;
/** 宗地最小进深（8 m 模数的一格）。 */
const MIN_DEPTH = 8;
export function generateParcel(parcel, all) {
    const empty = {
        buildings: 0,
        coverage: 0,
        far: 0,
        maxHeight: 0,
        setback: SETBACK,
        buildableArea: 0,
        freeArea: 0,
        streets: 0,
        zoning: 0,
        slots: 0,
    };
    const paths = areaPaths(parcel);
    const ring = paths?.[0];
    if (!ring || ring.length < 3)
        return { objects: [], stats: empty, warning: '地块没有有效轮廓' };
    const parcelArea = Math.abs(ringArea(ring));
    if (parcelArea < 200)
        return { objects: [], stats: empty, warning: '地块太小（< 200 m²），无法排布' };
    // 1) 局部坐标系：用最长边当 u 轴。宗地大多是矩形，这样切出来的带状宗地更整齐，
    //    而且"临街面"天然朝向脊街，不需要再做方向搜索。
    const frame = frameOf(ring);
    const locals = ring.map((p) => toLocal(p, frame));
    const su = locals.map((p) => p.x);
    const sv = locals.map((p) => p.y);
    const u0 = Math.min(...su);
    const u1 = Math.max(...su);
    const v0 = Math.min(...sv);
    const v1 = Math.max(...sv);
    const W = u1 - u0;
    const H = v1 - v0;
    if (W < SETBACK * 2 + 12 || H < SETBACK * 2 + MIN_DEPTH) {
        return { objects: [], stats: empty, warning: `地块太窄（有效 ${W.toFixed(0)}×${H.toFixed(0)} m），退线后放不下楼` };
    }
    const objects = [];
    const uid = `${parcel.id}`;
    // 2) 脊街：从地块一端贯通到另一端。位置按 seed 在中间三分之一内浮动，
    //    这样"换一版"（Shift+G）能看出差别，又不会把路甩到地块外。
    const rnd = mulberry32(parcel.seed);
    const vc = v0 + H * (0.35 + rnd() * 0.3);
    const roadW = 6;
    const road = {
        ...commonDefaults('road', `生成街道 · ${parcel.name}`),
        origin: 'generated',
        generatedFrom: uid,
        centerline: [
            toWorld({ x: u0 + SETBACK * 0.5, y: vc }, frame),
            toWorld({ x: u1 - SETBACK * 0.5, y: vc }, frame),
        ],
        roadClass: 'local',
        width: roadW,
        sidewalk: true,
    };
    objects.push(road);
    // 3) 两侧带状宗地。每一带是一个"沿街地块"，临街面就是脊街那一侧。
    const bands = [];
    const northDepth = v1 - SETBACK - (vc + roadW / 2 + STREET_MARGIN / 2);
    const southDepth = vc - roadW / 2 - STREET_MARGIN / 2 - (v0 + SETBACK);
    if (northDepth >= MIN_DEPTH) {
        bands.push({
            quad: quadOf(frame, u0 + SETBACK, vc + roadW / 2 + STREET_MARGIN / 2, u1 - SETBACK, v1 - SETBACK),
            dir: toWorldDir({ x: 0, y: -1 }, frame), // 朝街 = 朝南
        });
    }
    if (southDepth >= MIN_DEPTH) {
        bands.push({
            quad: quadOf(frame, u0 + SETBACK, v0 + SETBACK, u1 - SETBACK, vc - roadW / 2 - STREET_MARGIN / 2),
            dir: toWorldDir({ x: 0, y: 1 }, frame),
        });
    }
    if (!bands.length) {
        return { objects, stats: { ...empty, streets: 1 }, warning: `脊街两侧都放不下 ${MIN_DEPTH} m 进深的宗地` };
    }
    // 4) 铺装：把可建范围铺成硬质地面（含街道两侧的楼前场地）。
    //    没有这一层，2D 里地块是空的、3D 里楼浮在土上，两边都不像样。
    const pad = {
        ...commonDefaults('ground', `生成铺装 · ${parcel.name}`),
        origin: 'generated',
        generatedFrom: uid,
        surface: 'paving',
        boundary: [quadOf(frame, u0 + SETBACK * 0.5, v0 + SETBACK * 0.5, u1 - SETBACK * 0.5, v1 - SETBACK * 0.5)],
    };
    objects.push(pad);
    // 5) 宗地 → 楼位 → 楼。切分走 slots 的 cutZoning，和 C# 烘焙结果同一套规则。
    const spec = KIND_SPECS[parcel.zoneKind];
    const maxFoot = parcelArea * clamp01(parcel.density);
    const maxFloorArea = parcelArea * Math.max(0.1, parcel.far);
    let footSum = 0;
    let floorSum = 0;
    let tallest = 0;
    let slots = 0;
    let zoning = 0;
    for (let bi = 0; bi < bands.length; bi++) {
        const band = bands[bi];
        zoning++;
        // 宗地四边形 → PlanZoning（plan-ir 用 [x, z] 元组，这里只转一次）
        const quad = band.quad.map((p) => [p.x, p.y]);
        const cut = cutZoning({ zoneId: ZONE_PREFAB[parcel.zoneKind], quad }, parcel.zoneKind, [band.dir.x, band.dir.y], `z${parcel.seed}b${bi}`);
        for (const slot of cut.slots) {
            slots++;
            const foot = slot.width * slot.depth;
            if (footSum + foot > maxFoot)
                continue; // 密度上限：超了就不放，不留半栋
            const [f0, f1] = spec.floors;
            // 层数：KIND_SPECS 区间 → 被 heightRange 夹 → 被容积率压
            const auto = f0 + Math.floor(hash01(`${parcel.seed}:${slot.id}`) * (f1 - f0 + 1));
            let floors = clampInt(auto, f0, f1);
            const hMin = Math.ceil(parcel.heightRange[0] / spec.floorHeight);
            const hMax = Math.max(hMin, Math.floor(parcel.heightRange[1] / spec.floorHeight));
            floors = clampInt(floors, Math.max(f0, hMin), Math.min(f1, hMax));
            // 容积率：这栋楼加进去会不会超过总建筑面积上限
            while (floors > 1 && floorSum + foot * floors > maxFloorArea)
                floors--;
            if (floorSum + foot * floors > maxFloorArea && floorSum > 0)
                continue;
            const h = floors * spec.floorHeight;
            const b = {
                ...commonDefaults('building', `${zhOf(parcel.zoneKind)} ${floors}F`),
                origin: 'generated',
                generatedFrom: uid,
                footprint: [slot.quad.map((p) => ({ x: p[0], y: p[1] }))],
                height: h,
                floors,
                style: 'geometric',
                archetype: parcel.zoneKind,
                slotId: slot.id,
            };
            objects.push(b);
            footSum += foot;
            floorSum += foot * floors;
            tallest = Math.max(tallest, h);
        }
    }
    if (!floorSum)
        return { objects, stats: { ...empty, streets: 1, zoning, slots }, warning: '一个楼位都没排上，检查密度/容积率是否太小' };
    const buildableArea = bands.reduce((s, b) => s + Math.abs(ringArea(b.quad)), 0);
    const stats = {
        buildings: objects.filter((o) => o.kind === 'building').length,
        coverage: footSum / parcelArea,
        far: floorSum / parcelArea,
        maxHeight: tallest,
        setback: SETBACK,
        buildableArea,
        freeArea: Math.max(0, buildableArea - footSum),
        streets: 1,
        zoning,
        slots,
    };
    // 6) 出入口：如果这块地附近没有用户放的出入口，就在脊街端头补一个生成出入口。
    //    Placemaker 的 ready 靠出入口接街，缺了它整块地会"收地"（台账 D-023）。
    const hasGeneratedDoor = all.some((o) => o.kind === 'access' && o.generatedFrom === uid);
    const userDoorNear = all.some((o) => {
        if (o.kind !== 'access')
            return false;
        if (o.generatedFrom === uid)
            return false;
        return dist(o.pos, road.centerline[0]) < 40;
    });
    if (!hasGeneratedDoor && !userDoorNear) {
        const door = {
            ...commonDefaults('access', `生成出入口 · ${parcel.name}`),
            origin: 'generated',
            generatedFrom: uid,
            pos: road.centerline[0],
            width: 6,
            dir: Math.atan2(road.centerline[1].y - road.centerline[0].y, road.centerline[1].x - road.centerline[0].x),
            accessType: 'service',
            priority: 2,
        };
        objects.push(door);
    }
    const warning = footSum < maxFoot * 0.5
        ? `楼位只占到密度上限的 ${((footSum / maxFoot) * 100).toFixed(0)}% —— 宗地切分受宽度模数限制，不是密度参数没生效`
        : undefined;
    return { objects, stats, warning };
}
function frameOf(ring) {
    // 最长边当 u 轴
    let best = 0;
    let bi = 0;
    for (let i = 0; i < ring.length; i++) {
        const d = dist(ring[i], ring[(i + 1) % ring.length]);
        if (d > best) {
            best = d;
            bi = i;
        }
    }
    const o = ring[bi];
    const u = norm(sub(ring[(bi + 1) % ring.length], o));
    return { o, u };
}
function toLocal(p, f) {
    const d = sub(p, f.o);
    const v = perp(f.u);
    return { x: d.x * f.u.x + d.y * f.u.y, y: d.x * v.x + d.y * v.y };
}
function toWorld(p, f) {
    const v = perp(f.u);
    return { x: f.o.x + f.u.x * p.x + v.x * p.y, y: f.o.y + f.u.y * p.x + v.y * p.y };
}
function toWorldDir(p, f) {
    const v = perp(f.u);
    return norm({ x: f.u.x * p.x + v.x * p.y, y: f.u.y * p.x + v.y * p.y });
}
function quadOf(f, ua, va, ub, vb) {
    return [
        toWorld({ x: ua, y: va }, f),
        toWorld({ x: ub, y: va }, f),
        toWorld({ x: ub, y: vb }, f),
        toWorld({ x: ua, y: vb }, f),
    ];
}
// ─────────────────────────────────────────── 小工具
function hash01(s) {
    let h = 2166136261;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 16777619);
    }
    return ((h >>> 0) % 100000) / 100000;
}
function mulberry32(seed) {
    let a = seed >>> 0;
    return () => {
        a = (a + 0x6d2b79f5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}
const clamp01 = (v) => Math.max(0.05, Math.min(1, v));
const clampInt = (v, lo, hi) => Math.max(lo, Math.min(hi, Math.round(v)));
function zhOf(kind) {
    const m = {
        commercialLow: '低密度商业',
        commercialHigh: '高密度商业',
        mixed: '混合',
        residentialLow: '低密度住宅',
        residentialRow: '联排住宅',
        residentialMedium: '中密度住宅',
        residentialHigh: '高密度住宅',
        lowRent: '廉租住宅',
        officeLow: '低层办公',
        officeHigh: '高层办公',
        industrial: '工业',
    };
    return m[kind] ?? kind;
}
/** 地块包围盒（供 UI 显示"可建范围"）。 */
export function parcelBounds(p) {
    const ring = areaPaths(p)?.[0];
    if (!ring)
        return null;
    const b = boundsOfPaths([ring]);
    return { w: b.maxX - b.minX, h: b.maxY - b.minY };
}
