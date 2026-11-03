// Plan → three 场景对象。
//
// 数据来自 Placemaker 的七个输出通道（Plan IR）：surfaces / nets / objects / zoning。
// 这正是"看得懂"的关键——不是把规则画成表格，而是把**引擎真的会造出来的东西**立起来。
//
// 几个刻意的取舍：
//   · **只做体块（massing），不做模型替换**。楼就是楼位 quad 挤出到层高×层数，
//     和资产库当前的"盒子兜底"一致（docs/D 的 Q-010/Q-012）。换真模型是后面的事，
//     现在先把"这块地会长出什么"看清楚。
//   · **楼高按 KIND_SPECS 的层数区间 + id 的哈希**取，同一份方案每次打开都一样。
//     用 Math.random 会让"同一块地看起来每次不同"，那是调试灾难。
//   · **树/家具用 InstancedMesh**：一份方案 113~300 个物件，逐个 Mesh 是几百个
//     draw call；实例化后 3 个。
//   · **街道用 plan-ir 的 sampleBezier 采样**，和判定用同一条折线 —— 不会出现
//     "看着贴上了、规则没过"的错觉。
import { KIND_SPECS, cutZoning } from "../../slots/src/cut.js";
import { ZONE_PREFAB } from "../../plan-ir/src/types.js";
import { bezierOutline, flatRing, prism, ribbon } from "./geom.js";
// ─────────────────────────────────────────── 配色
//
// 一套就够，别做主题切换：颜色是为了**分辨图层**，不是为了好看。
// 涨跌色那一套在这里不适用（这是城市模型不是行情）。
const SURFACE_COLOR = {
    ground: 0x353c43,
    paving: 0x8f939a,
    asphalt: 0x4a525a,
    grass: 0x3f6b46,
    plaza: 0x9a9384,
    water: 0x2f628f,
    decorative: 0x7d7160,
};
const NET_COLOR = {
    street: 0x2b333d,
    avenue: 0x28303a,
    culDeSac: 0x2e363f,
    roundabout: 0x28303a,
    perimeterRoad: 0x2f3740,
    pathway: 0xb9a887,
    pathwayWide: 0xb2a181,
    driveAisle: 0x303841,
    driveConnector: 0x303841,
    alley: 0x323a43,
    fence: 0x5a4a3a,
    other: 0x384049,
};
/** 宽度（米）。Placemaker 的 net 不带宽度字段，按 role 查表——和 CS2 的路型对应。 */
const NET_WIDTH = {
    street: 12,
    avenue: 17,
    culDeSac: 9,
    roundabout: 12,
    perimeterRoad: 10,
    pathway: 2.6,
    pathwayWide: 4.2,
    driveAisle: 5.5,
    driveConnector: 6.5,
    alley: 4,
    fence: 0.35,
    other: 8,
};
const ZONE_COLOR = {
    commercialLow: 0xc9b489,
    commercialHigh: 0xb8a271,
    mixed: 0xc2a892,
    residentialLow: 0xb98d78,
    residentialRow: 0xb28570,
    residentialMedium: 0xa87d6a,
    residentialHigh: 0x9c7362,
    lowRent: 0xa8896f,
    officeLow: 0x8fa3b8,
    officeHigh: 0x7d95ae,
    industrial: 0x8a8f96,
};
// ─────────────────────────────────────────── 材质缓存
const matCache = new Map();
function mat(color, opts = {}) {
    const key = `${color}|${opts.rough ?? 0.9}|${opts.metal ?? 0}|${opts.transparent ?? 1}`;
    const hit = matCache.get(key);
    if (hit)
        return hit;
    const m = new THREE.MeshStandardMaterial({
        color,
        roughness: opts.rough ?? 0.9,
        metalness: opts.metal ?? 0,
        transparent: (opts.transparent ?? 1) < 1,
        opacity: opts.transparent ?? 1,
        flatShading: opts.flat ?? false,
    });
    matCache.set(key, m);
    return m;
}
// ─────────────────────────────────────────── 工具
/** FNV-1a → [0,1)。同一 id 每次结果相同，楼层数才稳定。 */
function hash01(s) {
    let h = 2166136261;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 16777619);
    }
    return ((h >>> 0) % 100000) / 100000;
}
const KIND_BY_PREFAB = new Map(Object.entries(ZONE_PREFAB).map(([kind, prefab]) => [prefab, kind]));
/** ZoneGrid 的 zoneId 是 prefab 名（"EU Commercial Low"），映射回我们的 kind。 */
export function kindOfZoneId(zoneId) {
    const direct = KIND_BY_PREFAB.get(zoneId);
    if (direct)
        return direct;
    const tail = zoneId.replace(/^(EU|NA|AS)\s+/i, '');
    for (const [prefab, k] of KIND_BY_PREFAB) {
        if (prefab.replace(/^(EU|NA|AS)\s+/i, '') === tail)
            return k;
    }
    return null;
}
function centroidOf(ring) {
    let x = 0;
    let z = 0;
    for (const p of ring) {
        x += p[0];
        z += p[1];
    }
    return [x / ring.length, z / ring.length];
}
/**
 * 宗地的临街方向：取"质心 → 最近街道采样点"的单位向量。
 *
 * 为什么不用 AttachmentFinder：它算的是**地块边界**的贴合点，而宗地是地块内部
 * 切出来的一小块，两者不是同一个环。这里只要一个"从宗地朝向街"的方向，够用了。
 */
function frontDirFor(quad, nets) {
    const c = centroidOf(quad);
    let best = [1, 0];
    let bestD = Infinity;
    for (const n of nets) {
        if (n.role === 'pathway' || n.role === 'pathwayWide' || n.role === 'fence')
            continue;
        const pts = bezierOutline(n.a, n.b, n.c, n.d);
        for (const p of pts) {
            const dx = p[0] - c[0];
            const dz = p[1] - c[1];
            const d = dx * dx + dz * dz;
            if (d < bestD) {
                bestD = d;
                const len = Math.hypot(dx, dz) || 1;
                best = [dx / len, dz / len];
            }
        }
    }
    return best;
}
// ─────────────────────────────────────────── 主构建
export function buildPlan(ch, ring, toggles, root, seedKey) {
    const stats = { surfaces: 0, nets: 0, objects: 0, markings: 0, slots: 0, footArea: 0, tallest: 0, longestStreet: 0 };
    const buildings = [];
    // 1) 地块底板：把轮廓挤出 0.6 m，让地块"浮"在网格上
    if (ring.length >= 3) {
        const pad = new THREE.Mesh(prism(ring, -0.6, 0.06), mat(0x20262c, { rough: 1 }));
        pad.receiveShadow = true;
        root.add(pad);
        // 轮廓描边，方便看地块范围
        const line = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(ring.map((p) => new THREE.Vector3(p[0], 0.1, p[1]))), new THREE.LineBasicMaterial({ color: 0x4d5a68 }));
        root.add(line);
    }
    // 2) 铺装 / 草地 / 水面
    if (toggles.surfaces) {
        for (const s of ch.surfaces) {
            if (s.ring.length < 3)
                continue;
            addSurface(s, root);
            stats.surfaces++;
        }
    }
    // 3) 街道 / 人行道
    if (toggles.streets) {
        for (const n of ch.nets) {
            const w = NET_WIDTH[n.role] ?? 8;
            const pts = bezierOutline(n.a, n.b, n.c, n.d);
            if (pts.length < 2)
                continue;
            let len = 0;
            for (let i = 1; i < pts.length; i++)
                len += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]);
            stats.longestStreet = Math.max(stats.longestStreet, len);
            // 路面抬到 0.12 m：避免和铺装面 z-fighting
            const isPath = n.role === 'pathway' || n.role === 'pathwayWide';
            const isFence = n.role === 'fence';
            if (isFence) {
                const fence = new THREE.Mesh(ribbon(pts, Math.max(w, 0.5), 0.5), mat(NET_COLOR.fence, { rough: 0.9 }));
                root.add(fence);
                stats.nets++;
                continue;
            }
            if (isPath) {
                const m = new THREE.Mesh(ribbon(pts, w, 0.09), mat(NET_COLOR[n.role] ?? 0xb9a887, { rough: 0.95 }));
                m.receiveShadow = true;
                root.add(m);
                stats.nets++;
                continue;
            }
            // 街道分带：先铺一条比车行道宽 3.2 m 的浅色带（= 两侧各 1.6 m 人行道），
            // 再把车行道盖在中间（抬高 3 cm）。这样一条街才有"路 + 人行道"的读感，
            // 否则只是一条灰色带子，分不出哪是路、哪是地。
            const walk = new THREE.Mesh(ribbon(pts, w + 3.2, 0.1), mat(0x9aa1a8, { rough: 1 }));
            walk.receiveShadow = true;
            root.add(walk);
            const road = new THREE.Mesh(ribbon(pts, w, 0.13), mat(NET_COLOR[n.role] ?? 0x33393f, { rough: 0.95 }));
            road.receiveShadow = true;
            root.add(road);
            // 车道中心线：太窄的路画了反而脏
            if (w >= 9) {
                const cl = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts.map((p) => new THREE.Vector3(p[0], 0.15, p[1]))), new THREE.LineBasicMaterial({ color: 0x7c8794, transparent: true, opacity: 0.45 }));
                root.add(cl);
            }
            stats.nets++;
        }
    }
    // 4) 宗地：半透明底面 + 虚线描边
    if (toggles.zoning) {
        for (const z of ch.zoning) {
            const quad = z.quad;
            if (quad.length < 3)
                continue;
            const fill = new THREE.Mesh(flatRing(quad, 0.14), mat(0x8b7bd8, { transparent: 0.16, rough: 1 }));
            root.add(fill);
            const loop = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(quad.map((p) => new THREE.Vector3(p[0], 0.16, p[1]))), new THREE.LineDashedMaterial({ color: 0xa795ff, dashSize: 1.6, gapSize: 1.2 }));
            loop.computeLineDistances();
            root.add(loop);
        }
    }
    // 5) 楼块：宗地 → 楼位（这次终于看得出"这条街两边的楼是什么样"）
    if (toggles.massing) {
        ch.zoning.forEach((z, i) => {
            const kind = kindOfZoneId(z.zoneId);
            if (!kind)
                return;
            const quad = z.quad;
            const dir = frontDirFor(quad, ch.nets);
            const cut = cutZoning({ zoneId: z.zoneId, quad: z.quad }, kind, dir, `z${i}`);
            for (const slot of cut.slots) {
                const spec = KIND_SPECS[slot.kind];
                const [f0, f1] = spec.floors;
                const floors = f0 + Math.floor(hash01(`${seedKey}:${slot.id}`) * (f1 - f0 + 1));
                const h = floors * spec.floorHeight;
                const mesh = new THREE.Mesh(prism(slot.quad, 0.1, h + 0.1), mat(ZONE_COLOR[slot.kind] ?? 0xb0a080, { rough: 0.85 }));
                mesh.castShadow = true;
                mesh.receiveShadow = true;
                mesh.userData = { kind: slot.kind, floors, height: h, id: slot.id, w: slot.width, d: slot.depth };
                root.add(mesh);
                buildings.push(mesh);
                stats.slots++;
                stats.footArea += slot.width * slot.depth;
                stats.tallest = Math.max(stats.tallest, h);
            }
        });
    }
    // 6) 物件：树 / 家具 / 标线 —— 三类实例化
    if (toggles.props || toggles.markings) {
        const r = addProps(ch.objects, root, toggles);
        stats.objects = r.total;
        stats.markings = r.markings;
    }
    return { stats, buildings };
}
function addSurface(s, root) {
    const y = 0.03 + (s.y ?? 0) * 0.001;
    const color = SURFACE_COLOR[s.role] ?? 0x555a60;
    const opaque = s.role !== 'water';
    const m = new THREE.Mesh(flatRing(s.ring, y), opaque ? mat(color, { rough: s.role === 'water' ? 0.25 : 1 }) : mat(color, { rough: 0.15, metal: 0.4 }));
    m.receiveShadow = true;
    root.add(m);
}
const TREE_ROLES = new Set(['tree', 'bush', 'planter']);
const FLAT_ROLES = new Set(['parkingBay', 'decal', 'arrow', 'marker', 'accessPoint', 'culDeCap', 'roundaboutIsland']);
/**
 * 物件分三类实例化。
 * `parkingBay` 这类是地面标线，画成贴地的扁片；树是锥体+柱；其余是小方块。
 * 标线归 `toggles.markings`，绿植/家具归 `toggles.props` —— 两组独立开关。
 */
function addProps(objects, root, toggles) {
    if (!objects.length)
        return { total: 0, markings: 0 };
    const trees = toggles.props ? objects.filter((o) => TREE_ROLES.has(o.role)) : [];
    const flats = toggles.markings ? objects.filter((o) => FLAT_ROLES.has(o.role)) : [];
    const props = toggles.props
        ? objects.filter((o) => !TREE_ROLES.has(o.role) && !FLAT_ROLES.has(o.role))
        : [];
    const dummy = new THREE.Object3D();
    if (trees.length) {
        // 三种绿植分开画：乔木（锥+柱）、灌木（球）、花池（扁盒）。
        // 全用锥体的话，一整片绿地看起来像插了一地路障 —— 形状是读得出"这是什么"的第一层信息。
        const tall = trees.filter((o) => o.role === 'tree');
        const bush = trees.filter((o) => o.role === 'bush');
        const planter = trees.filter((o) => o.role === 'planter');
        if (tall.length) {
            const canopy = new THREE.InstancedMesh(new THREE.ConeGeometry(1, 1, 7), mat(0x3f7a4a, { rough: 0.95 }), tall.length);
            const trunk = new THREE.InstancedMesh(new THREE.CylinderGeometry(0.16, 0.22, 1, 5), mat(0x4a3a2c), tall.length);
            canopy.castShadow = true;
            trunk.castShadow = true;
            tall.forEach((o, i) => {
                const s = o.scale ?? 1;
                const trunkH = 2.2 * s;
                const canopyH = 3.6 * s;
                const y = (o.y ?? 0) + 0.1;
                dummy.position.set(o.x, y + trunkH / 2, o.z);
                dummy.scale.set(1, trunkH, 1);
                dummy.updateMatrix();
                trunk.setMatrixAt(i, dummy.matrix);
                dummy.position.set(o.x, y + trunkH + canopyH / 2, o.z);
                dummy.scale.set(1.5 * s, canopyH, 1.5 * s);
                dummy.updateMatrix();
                canopy.setMatrixAt(i, dummy.matrix);
            });
            root.add(canopy, trunk);
        }
        if (bush.length) {
            const g = new THREE.SphereGeometry(1, 8, 6);
            const m = new THREE.InstancedMesh(g, mat(0x4b8a55, { rough: 1 }), bush.length);
            m.castShadow = true;
            bush.forEach((o, i) => {
                const s = o.scale ?? 1;
                dummy.position.set(o.x, (o.y ?? 0) + 0.9 * s, o.z);
                dummy.scale.set(1.1 * s, 0.8 * s, 1.1 * s);
                dummy.updateMatrix();
                m.setMatrixAt(i, dummy.matrix);
            });
            root.add(m);
        }
        if (planter.length) {
            const g = new THREE.BoxGeometry(1.9, 0.9, 1.9);
            const m = new THREE.InstancedMesh(g, mat(0x8a7f6d, { rough: 1 }), planter.length);
            m.castShadow = true;
            planter.forEach((o, i) => {
                const s = o.scale ?? 1;
                dummy.position.set(o.x, (o.y ?? 0) + 0.45 * s, o.z);
                dummy.scale.setScalar(s);
                dummy.rotation.set(0, o.yaw ?? 0, 0);
                dummy.updateMatrix();
                m.setMatrixAt(i, dummy.matrix);
            });
            root.add(m);
        }
    }
    if (flats.length) {
        const g = new THREE.PlaneGeometry(2.4, 5);
        g.rotateX(-Math.PI / 2);
        const m = new THREE.InstancedMesh(g, mat(0xd8dde3, { transparent: 0.55, rough: 1 }), flats.length);
        flats.forEach((o, i) => {
            dummy.position.set(o.x, 0.13, o.z);
            dummy.rotation.set(0, o.yaw ?? 0, 0);
            dummy.scale.setScalar((o.scale ?? 1) * 0.9);
            dummy.updateMatrix();
            m.setMatrixAt(i, dummy.matrix);
        });
        root.add(m);
    }
    if (props.length) {
        const g = new THREE.BoxGeometry(0.7, 1, 0.7);
        const m = new THREE.InstancedMesh(g, mat(0x9aa4ae), props.length);
        m.castShadow = true;
        props.forEach((o, i) => {
            dummy.position.set(o.x, 0.5, o.z);
            dummy.rotation.set(0, o.yaw ?? 0, 0);
            dummy.scale.setScalar(o.scale ?? 1);
            dummy.updateMatrix();
            m.setMatrixAt(i, dummy.matrix);
        });
        root.add(m);
    }
    return { total: trees.length + flats.length + props.length, markings: flats.length };
}
