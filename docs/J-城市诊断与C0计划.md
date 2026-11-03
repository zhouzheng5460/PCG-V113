# C0 · Urban Composer（城市规划层）

> 背景：Site Generator（Parking / Plaza）已经跑通，但**城市整体看起来不对**。
> 问题不在"少一个广场生成器"，而在**上游没有规划层**：中心在哪、为什么这里是高层、
> 为什么工业贴着住宅 —— 这些都没有依据。
>
> 用户 2026-11-03 的判断：*"现在最缺的不是第三个、第四个 Site Generator，而是一个真正的
> Urban Composer / City Planning Layer。"* 顺序固定为：
>
> ```
> C0.0 Urban Recon（先量，不猜）
>   ↓ C0.1 真 District 模型（不用 k-means）
>   ↓ C0.2 中心场 urbanIntensity
>   ↓ C0.3 高度 / 密度场
>   ↓ C0.4 道路分级
> ```

---

## C0.0 城市诊断仪器（**已完成**）

### 怎么跑

```bash
cd vendor/townforge
node node_modules/tsx/dist/cli.mjs src/diag/cli.ts                 # 15 街区 / 默认种子
node node_modules/tsx/dist/cli.mjs src/diag/cli.ts 40 --check      # 大城 + 门禁
node node_modules/tsx/dist/cli.mjs src/diag/cli.ts --out x.html    # 指定输出
```

产出两样东西：

1. **控制台诊断表**（四组头部指标 + 常规体检）
2. **视觉 Gate**：`tools/urban/urban-recon.html` —— **只画规划层**：
   ① 用途色块 ② 道路等级 ③ 高度热力

> 用户定的规矩：**"关闭建筑 Mesh，只显示 District 色块 + 道路等级 + 高度热力图。
> 如果这一张二维规划图都不好看，禁止打开 Building / Parking / Plaza 来掩盖问题。"**

### 四个头部指标（怎么算）

| 指标 | 定义 | 为什么这么算 |
| --- | --- | --- |
| `landUseCoherence` | ①相邻同用途比例：每块地"邻居里同用途"的均值；②代理片区纯度：k-means(k=6) 簇内最大用途占比 | ①不需要 district 概念就能测"撒芝麻"；②是真 district 模型（C0.1）就位前的**代理口径**，别当结论 |
| `neighborHeightJump` | 相邻街区最高楼高之差：p50 / p90 / max | 直接对应"20 m 挨着 180 m"那种视觉灾难 |
| `roadHierarchyRatio` | 按 `model.edgeClass` 统计 arterial / collector / local 的**长度**占比 | 模型里本来就有三级，只是从没按长度量过 |
| `districtTransition` | 相邻异用途对里"不兼容组合"的占比（工业↔住宅、CBD↔住宅、停车↔公园…） | 兼容矩阵是**显式清单**，不是打分函数，改起来有据可查 |

### 基线（n=15 / seed=682063530）

```
用途     相邻同用途 37% · 代理纯度 51%
         中央商务 14% · 商业 18% · 住宅 48% · 工业 7% · 公共 6% · 公园 1% · 停车 2% · 绿地 4%
高度     相邻高差 p50 4.2 / p90 20.6 / max 34.4 m
         楼高 mean 24.2 / p50 23.3 / p90 39.4 / max 48.9 m
道路     主干 12%（2018 m）· 次干 55%（9128 m）· 支路 33%（5399 m）
过渡     不兼容相邻 22/152 = 14%
体检     街区 p50 7421 m² · 宗地 p50 1005 m² · 覆盖 12% · FAR 0.93 · 开敞 4%
```

n=40 更差：纯度 **46%**、高差 p90 **24.3 m**、开敞 9%。

### 视觉 Gate 一眼看到的三件事

| 面板 | 看到什么 | 说明 |
| --- | --- | --- |
| ① 用途色块 | 黄/红/蓝/橙碎成一格一格 | **没有一片成规模的住宅区** —— 撒芝麻 |
| ② 道路等级 | 一横一竖两条橙线，其余全银/灰 | 骨架只有 2 条，城市靠毛细血管撑着 |
| ③ 高度热力 | 中心一坨红，紧挨就是紫/黑 | **只有峰值没有梯度**，边缘断崖 |

### 门禁（`--check`，基线 6 项过 2）

| 目标 | 阈值 | 基线 |
| --- | --- | --- |
| 同用途纯度 | ≥ 70% | **51%** ✗ |
| 相邻高差 p90 | ≤ 20 m | **20.6 m** ✗（差一点） |
| 主干占比 | ≥ 8% | 12% ✓ |
| 支路占比 | ≤ 40% | 33% ✓ |
| 不兼容相邻 | ≤ 10% | **14%** ✗ |
| 开敞空间 | ≥ 10% | **4%** ✗ |

**现在全不达标是正常的** —— 这版是基线。它的用途是：C0.1~C0.4 每改一步，都用同一把尺子量。

---

## 下一步（按依赖顺序，不要跳）

| 阶段 | 做什么 | 验收 |
| --- | --- | --- |
| **C0.1** | **真 District 模型**（不是 k-means）：`UrbanDistrict { polygon, type, center, density, heightBand, greenRatio, roadIntensity }`，类型先只要 6 个（CBD / mixed / residential / industrial / suburban / park） | 纯度 ≥ 70%；district 有 polygon、可被后续场继承 |
| **C0.2** | **中心场** `urbanIntensity(x,y) = 中心吸引 + 主干道吸引 − 距离衰减 − 边缘衰减`；District 的密度/高度/路密/用途概率全部从它继承 | 高度热力图出现"中心→次中心→外围"梯度 |
| **C0.3** | **高度/密度场**：`targetHeight = districtBase × urbanIntensity × roadAccess` + 邻域平滑（2~3 轮 `h = 0.6h + 0.4·mean(邻居)`） | 相邻高差 p90 明显下降 |
| **C0.4** | **道路分级**：**不重写路网生成**，先给已有边赋级（跨 district → L0/L1；district 内主连接 → L2；只服务宗地 → L3），再让等级反过来参与生成 | 四级都存在、支路不承担跨区连接 |

## 明确不做

1. **不先做 FormalGarden Native**（用户 2026-11-03：只做侦察、不实现）。侦察已完成：
   garden 有 nets（pathway 524 m + fence 432 m）、**对称性不是镜像**（24 个花坛镜像配对 0/24）、
   core 不是区域（`areas` 为空）。语料 66 份已就位，随时可续。
2. **不为"让图好看"加视觉参数**。先让二维规划图好看，再开建筑。
3. **不在 C0.1 之前动 Site Generator**。
