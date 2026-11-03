// 原生生成器注册表。**这里列出来的才算"已迁移"**，其余一律走 golden。
//
// 为什么要一张显式的表而不是自动扫描目录：迁移进度必须是**可数的事实**，
// 不能靠"import 了就算"。这张表配合 `cli/status.ts` 直接给出"31 个接入生成器里迁移了几个"。

import type { SiteGenerator } from '../types.ts';
import { parking } from './parking.ts';
import { plaza } from './plaza.ts';

export const NATIVE_GENERATORS: readonly SiteGenerator[] = [parking, plaza];

export const NATIVE_BY_ID: ReadonlyMap<string, SiteGenerator> = new Map(
  NATIVE_GENERATORS.map((g) => [g.id, g]),
);
