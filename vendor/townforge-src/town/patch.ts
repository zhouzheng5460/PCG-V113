// 地块。原版 Patch 还带 withinWalls（是否在城墙内），城墙体系已随中世纪内容一起移除。

import { Pt } from '../core/geom';
import { Polygon } from '../core/polygon';
import type { Region } from '../core/voronoi';
import type { Ward } from './wards';
import type { ZonePurpose } from './zones';

export class Patch {
  shape: Polygon;
  ward: Ward | null = null;
  withinCity = false;
  /** 被禁建区整块占据（仍然产出地坪，只是不排建筑/不做用途分档） */
  blocked = false;
  /** 禁绘区的内容类型；普通地块为 undefined */
  purpose?: ZonePurpose;

  constructor(vertices: Pt[]) {
    this.shape = new Polygon(vertices);
  }

  static fromRegion(r: Region): Patch {
    return new Patch(r.vertices.map((tr) => tr.c));
  }
}
