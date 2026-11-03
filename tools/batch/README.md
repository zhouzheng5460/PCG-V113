# 批量回归夹具

这个目录里的 JSON **不是我们造的**，是从 CITYSKYLINE 那边的真实烘焙产物里搬过来的。
搬过来是为了让"街道契约"和"切楼规则"能在**真实城市几何**上跑回归，而不是只在我们自己造的
四个规整用例上自娱自乐。

## cases/ —— 54 份真实地块（烘焙输入）

来源：`CITYSKYLINE/townforge-enhance/tmp/pm-cases/*.json`

每份就是烘焙工具当时**原样喂给 C# Driver** 的输入：

```json
{
  "generator": "parking",
  "seed": 1234,
  "shape": { "ring": [[x, z], ...] },
  "streets": [{ "id": 1000, "a": [x, z], "b": [x, z], "width": 20, "sidewalk": true, "speed": 50 }, ...]
}
```

- `ring` 是**可建区边界**（`patch.shape` 按各路宽内缩后的结果，即路缘线），实测 54/54 都是逆时针
- `streets` 的每条边就是 `patch.shape` 的一条边——"道路中线就是地块边"，宽取该边红线宽
- 城市参数：`city24 s8`（24 个 patch，种子 8），主要来自 patch 42 / 90 / 0 / 1

**注意**：54 份里不同地块几何只有 **20 个**——同一个街区被 20+ 个生成器重复烘焙了一遍。
所以别把"54 份都通过"当成 54 个独立样本。报告首屏会写出去重后的数量。

## plans/ —— 4 份真实方案（含宗地）

来源：`CITYSKYLINE/townforge-enhance/src/bridge/data/*.plan.json`，只挑了 `zoning` 非空的四份：

| 方案 | 宗地 | 说明 |
| --- | --- | --- |
| mallPark | 5 | 商场旁停车 + 贴街宗地 |
| outdoorMall | 8 | |
| shopsPark | 12 | `streetShops=true`，两侧都是商铺 |
| stripMall | 6 | 宗地最深（33 m） |

这几份的 `zoning[].quad` 是 **C# 引擎真算出来的**宗地四边形（实测全是标准矩形，
局部框/面积 = 1.000，四角直角误差 0.000），直接拿来验"切楼位"那一段。

## 怎么用

```bash
node packages/demo/src/batch.ts              # 全跑（含 54 次 dotnet --attachments，约 1 分钟）
node packages/demo/src/batch.ts --no-csharp   # 跳过 C# 对照，秒级
node packages/demo/src/batch.ts --only=mall   # 只跑名字含 mall 的
```

产物：`packages/demo/out/batch.html`，中间文件在 `tmp/batch/`。

## 为什么把夹具复制进来而不是原地引用

1. 回归要能脱离另一个仓库独立跑（那边随时会改、会被 `git clean` 掉）
2. 这 580 KB 是**固定基线**：改了规则之后重跑，数字要和上次比
3. 它们同时是"真实城市长什么样"的证据——尤其是那些 2.7 m 的斜接短边
