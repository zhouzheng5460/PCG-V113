import { describe, expect, it } from 'vitest';
import * as THREE from 'three';
import { buildSceneIR, toShapes, ELEV } from '../core/sceneIR';
import { addTo, demoProject, emptyProject, makeBuilding, makeGround, makeParcel, makeRoad } from '../core/factory';
import { rectRing } from '../core/polygon';
import { v2 } from '../core/vec';
import { ThreeRealizer, geomKey, toV3 } from '../editor/three/realizer';

const sq = (x: number, y: number, w: number, h: number) => rectRing(v2(x, y), v2(x + w, y + h));

describe('SceneIR · Paths → ShapeSpec', () => {
  it('单环 → 1 个 ShapeSpec，无洞', () => {
    const r = toShapes([sq(0, 0, 10, 10)]);
    expect(r).toHaveLength(1);
    expect(r[0].holes).toHaveLength(0);
    expect(r[0].outer).toHaveLength(4);
  });

  it('回字形（洞与外环形心重合）不得把外环误判成洞', () => {
    // 20×20 外环 + 5..15 内洞，两者形心都是 (10,10)
    const r = toShapes([sq(0, 0, 20, 20), sq(5, 5, 10, 10)]);
    expect(r).toHaveLength(1);
    expect(r[0].holes).toHaveLength(1);
    expect(r[0].holes[0]).toHaveLength(4);
  });

  it('两个不相交的外环 → 2 个 ShapeSpec', () => {
    const r = toShapes([sq(0, 0, 10, 10), sq(30, 0, 10, 10)]);
    expect(r).toHaveLength(2);
    expect(r.every((s) => s.holes.length === 0)).toBe(true);
  });

  it('两个外环各带一个洞 → 2 个 ShapeSpec，各 1 个洞', () => {
    const r = toShapes([sq(0, 0, 20, 20), sq(5, 5, 5, 5), sq(40, 0, 20, 20), sq(45, 5, 5, 5)]);
    expect(r).toHaveLength(2);
    expect(r.map((s) => s.holes.length).sort()).toEqual([1, 1]);
  });

  it('三层嵌套：外环 / 洞 / 岛（even-odd 语义）', () => {
    const r = toShapes([sq(0, 0, 100, 100), sq(10, 10, 80, 80), sq(20, 20, 20, 20)]);
    expect(r).toHaveLength(2);
    const withHole = r.find((s) => s.holes.length === 1)!;
    expect(Math.min(...withHole.holes[0].map((p) => p.x))).toBeCloseTo(10, 6);
    const island = r.find((s) => s.holes.length === 0)!;
    expect(Math.min(...island.outer.map((p) => p.x))).toBeCloseTo(20, 6);
  });
});

describe('SceneIR · buildSceneIR', () => {
  it('示例项目：按语义产出对应节点类型与统计', () => {
    const ir = buildSceneIR(demoProject());
    const types = ir.nodes.map((n) => n.type);
    expect(types).toContain('extrude'); // 建筑
    expect(types).toContain('surface'); // 地块
    expect(types).toContain('ribbon'); // 道路
    expect(types).toContain('marker'); // 出入口
    expect(types).toContain('line'); // 园区边界
    expect(ir.stats.buildings).toBe(2);
    expect(ir.stats.maxHeight).toBeCloseTo(14, 6);
    expect(ir.stats.footprintArea).toBeGreaterThan(0);
  });

  it('不可见对象不进入 3D（2D 隐藏 = 3D 不渲染）', () => {
    const base = emptyProject();
    const b = makeBuilding([sq(0, 0, 10, 10)], { height: 8 });
    const p = addTo(base, b);
    const hidden = { ...p, objects: { ...p.objects, [b.id]: { ...b, visible: false } } };
    expect(buildSceneIR(p).nodes).toHaveLength(1);
    expect(buildSceneIR(hidden).nodes).toHaveLength(0);
  });

  it('地面叠层高度递增，避免 z-fighting', () => {
    expect(ELEV.parcel).toBeLessThan(ELEV.nobuild);
    expect(ELEV.nobuild).toBeLessThan(ELEV.road);
    expect(ELEV.road).toBeLessThan(ELEV.site);
    expect(ELEV.site).toBeLessThan(ELEV.access);
  });

  it('面状对象各自带正确 elevation，且铺装/绿地/停车分层不打架', () => {
    let p = emptyProject();
    p = addTo(p, makeParcel([sq(0, 0, 50, 50)]));
    p = addTo(p, makeGround([sq(0, 0, 20, 20)], 'paving'));
    p = addTo(p, makeGround([sq(30, 0, 50, 20)], 'lawn'));
    p = addTo(p, makeGround([sq(0, 30, 20, 50)], 'parking'));
    const ir = buildSceneIR(p);
    const elev = (kind: string, surface?: string) => {
      const n = ir.nodes.find((x) => x.kind === kind && (surface ? x.type === 'surface' && x.surface === surface : true));
      return n && n.type === 'surface' ? n.elevation : -1;
    };
    expect(elev('parcel')).toBeCloseTo(ELEV.parcel, 6);
    expect(elev('ground', 'paving')).toBeCloseTo(ELEV.paving, 6);
    expect(elev('ground', 'lawn')).toBeCloseTo(ELEV.lawn, 6);
    expect(elev('ground', 'parking')).toBeCloseTo(ELEV.parking, 6);
    // 叠层顺序：地块 < 绿地 < 铺装 < 停车 < 道路
    expect(ELEV.parcel).toBeLessThan(ELEV.lawn);
    expect(ELEV.lawn).toBeLessThan(ELEV.paving);
    expect(ELEV.paving).toBeLessThan(ELEV.parking);
    expect(ELEV.parking).toBeLessThan(ELEV.road);
  });

  it('道路节点保留中心线与宽度（几何交给 Realizer 生成包络）', () => {
    let p = emptyProject();
    p = addTo(p, makeRoad([v2(0, 10), v2(60, 10)], { width: 8 }));
    const road = buildSceneIR(p).nodes[0];
    expect(road.type).toBe('ribbon');
    if (road.type === 'ribbon') {
      expect(road.width).toBe(8);
      expect(road.centerline).toHaveLength(2);
    }
  });

  it('空项目：节点为空、bounds 无效', () => {
    const ir = buildSceneIR(emptyProject());
    expect(ir.nodes).toHaveLength(0);
    expect(ir.stats.buildings).toBe(0);
    expect(Number.isFinite(ir.bounds.minX)).toBe(false);
  });
});

describe('ThreeRealizer · 换轴与增量更新', () => {
  /** 世界 (x,y,z) → three (x, z, −y)：这一条错了整个 3D 都会镜像 */
  it('toV3 换轴：北(+y) 映射到 three 的 −z', () => {
    const v = toV3({ x: 3, y: 5 }, 2);
    expect(v.x).toBe(3);
    expect(v.y).toBe(2);
    expect(v.z).toBe(-5);
  });

  it('建筑挤出后包围盒与输入一致（x/y 换轴、z 抬成高度）', () => {
    const r = new ThreeRealizer();
    let p = emptyProject();
    const b = addTo(p, makeBuilding([sq(10, 30, 10, 10)], { height: 12 }));
    const ir = buildSceneIR(b);
    r.update(ir);

    const box = r.nodeBox(b.order[0])!;
    expect(box.min.x).toBeCloseTo(10, 3);
    expect(box.max.x).toBeCloseTo(20, 3);
    expect(box.min.z).toBeCloseTo(-40, 3);
    expect(box.max.z).toBeCloseTo(-30, 3);
    expect(box.min.y).toBeCloseTo(0, 3);
    expect(box.max.y).toBeCloseTo(12, 3);
    r.dispose();
  });

  it('geoKey 只在几何相关字段变化时才变（改名不算）', () => {
    const b = makeBuilding([sq(0, 0, 10, 10)], { height: 10 });
    const ir1 = buildSceneIR(addTo(emptyProject(), b));
    const renamed = { ...b, name: '改个名字' };
    const ir2 = buildSceneIR(addTo(emptyProject(), renamed));
    const taller = buildSceneIR(addTo(emptyProject(), { ...b, height: 30 }));
    expect(geomKey(ir1.nodes[0])).toBe(geomKey(ir2.nodes[0]));
    expect(geomKey(ir1.nodes[0])).not.toBe(geomKey(taller.nodes[0]));
  });

  it('删除的对象从场景中移除，其余复用（不重建全部）', () => {
    const r = new ThreeRealizer();
    const a = makeBuilding([sq(0, 0, 10, 10)], { height: 10, name: 'A' });
    const b = makeBuilding([sq(20, 0, 10, 10)], { height: 10, name: 'B' });
    let p = addTo(emptyProject(), a);
    p = addTo(p, b);
    r.update(buildSceneIR(p));
    expect(r.nodeCount()).toBe(2);

    const objA = r.pickables()[0];
    const p2 = { ...p, objects: { [a.id]: a }, order: [a.id] };
    r.update(buildSceneIR(p2));
    expect(r.nodeCount()).toBe(1);
    // A 的对象图应当被复用（同一实例），证明是增量 patch
    expect(r.pickables()[0]).toBe(objA);
    r.dispose();
  });

  it('选中只换材质，不影响几何对象', () => {
    const r = new ThreeRealizer();
    const b = makeBuilding([sq(0, 0, 10, 10)], { height: 10 });
    const p = addTo(emptyProject(), b);
    r.update(buildSceneIR(p));
    const before = r.nodeBox(b.id)!.max.y;
    r.setSelection([b.id]);
    const after = r.nodeBox(b.id)!.max.y;
    expect(after).toBeCloseTo(before, 6);
    // 选中材质带有青色自发光
    const mesh = (r.pickables()[0] as THREE.Group).children[0] as THREE.Mesh;
    const mat = mesh.material as THREE.MeshStandardMaterial;
    expect(mat.emissive.getHex()).toBe(0x22d3ee);
    r.dispose();
  });

  it('带洞的楼板：外环与洞都能进入 BufferGeometry', () => {
    const r = new ThreeRealizer();
    const p = addTo(emptyProject(), makeBuilding([sq(0, 0, 30, 30), sq(10, 10, 10, 10)], { height: 6 }));
    r.update(buildSceneIR(p));
    const mesh = (r.pickables()[0] as THREE.Group).children[0] as THREE.Mesh;
    const pos = mesh.geometry.getAttribute('position');
    expect(pos.count).toBeGreaterThan(24); // 有洞时三角面更多
    r.dispose();
  });
});
