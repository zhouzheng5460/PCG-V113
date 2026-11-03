// packages/scene-ir —— Plan IR 与 three 之间的那一层。
//
// 为什么单独成包：它必须**能被 node 直接跑**（离线算指纹、跑增量回归），
// 所以这个包里一个 `THREE` 都不许出现。渲染相关的全在 `packages/view3d/src/realizer.ts`。
export * from "./types.js";
export * from "./palette.js";
export * from "./from-plan.js";
