// three 通过 window.THREE 提供（由 tools/inline-three.mjs 把 ESM 构建产物降维成经典脚本）。
//
// 为什么是 any 而不是完整类型：本机没有 npm 网络，装不了 @types/three；
// 手写一份覆盖全部 API 的声明不值得。原型阶段只用到十几个类，拼错名字会在
// 运行时立刻暴露（白屏/报错），所以先不追求类型安全——**代价写在这里，别假装没有**。
//
// 想要类型的话：把 three 的 src/*.d.ts 拿进来，或等能装 @types/three 时替换这一行。
declare const THREE: any;
