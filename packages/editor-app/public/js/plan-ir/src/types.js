// Placemaker 的输入契约与输出契约（Plan IR）——两侧唯一共用的类型。
//
// 来源：vendor/placemaker/Core/Contracts.cs（输入）+ tools/oracle/driver/Emit.cs（输出的 JSON 协议）。
// 命名刻意贴近 C#，方便逐字段对照移植；不贴的地方是 JS 习惯（Vec2 用元组）。
//
// 三条硬约定（照抄桥接层注释，别改）：
//   1) 单位一律米。换算必须在调用方做。
//   2) 平面写 [x, z]，+z 是北，两个轴都不翻转（翻了是镜像，车位朝向全反）。
//   3) yaw 绕 +Y，0 = 朝 +Z。
export const ZONE_KINDS = [
    'commercialLow',
    'commercialHigh',
    'mixed',
    'residentialLow',
    'residentialRow',
    'residentialMedium',
    'residentialHigh',
    'lowRent',
    'officeLow',
    'officeHigh',
    'industrial',
];
/** kind → CS2 prefab id（计划里写 EU，游戏层按城市主题换前缀）。 */
export const ZONE_PREFAB = {
    commercialLow: 'EU Commercial Low',
    commercialHigh: 'EU Commercial High',
    mixed: 'EU Residential Mixed',
    residentialLow: 'EU Residential Low',
    residentialRow: 'EU Residential Medium Row',
    residentialMedium: 'EU Residential Medium',
    residentialHigh: 'EU Residential High',
    lowRent: 'Residential LowRent',
    officeLow: 'Office Low',
    officeHigh: 'Office High',
    industrial: 'Industrial Manufacturing',
};
/** 界面中文名（docs/C 术语表 §2.3）。 */
export const ZONE_LABEL = {
    commercialLow: '低密度商业',
    commercialHigh: '高密度商业',
    mixed: '混合用地',
    residentialLow: '低密度住宅',
    residentialRow: '联排住宅',
    residentialMedium: '中密度住宅',
    residentialHigh: '高密度住宅',
    lowRent: '廉租住宅',
    officeLow: '低层办公',
    officeHigh: '高层办公',
    industrial: '工业制造',
};
export function isZoneKind(s) {
    return ZONE_KINDS.includes(s);
}
