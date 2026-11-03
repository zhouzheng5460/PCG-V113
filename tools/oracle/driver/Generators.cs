// 生成器家族导出器：把 130 个 [AreaGenerator] 的"ready 归谁判、建议归谁给"反射出来。
//
// 为什么必须反射而不是 grep 源码：
//   family 不是"类叫什么名"，而是三个事实的组合 ——
//     ① CheckReady / SuggestMarkers **实际声明在哪个类**（虚方法链上最近的一个）
//     ② UsesEntrances / TypedEntrances 这两个 protected virtual 开关在**这个实例上**的值
//     ③ LayerAlias 这类委托层要穿透到 canonical id
//   ② 是运行时值，grep 看不到；③ 是映射表，grep 会漏。手抄 130 行必然漂移，
//   上一轮 ready.ts 里的 32 行手写表就是这么来的（并且 plaza 抄错了，见 docs/D 的 D-024）。
//
// 这个命令只输出**事实**，不做 family 归类 —— 归类规则属于文档知识，放在 TS 侧
// （packages/street-rules/tools/gen-entrance-classes.mjs），改了规则不用重编 C#。
//
// 用法：PlacemakerDriver --generators <generators.json>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Placemaker.Core;
using Placemaker.Core.Layers.Common;

namespace Placemaker.Driver
{
    internal static class Generators
    {
        private sealed class Row
        {
            public string id { get; set; }
            public string kind { get; set; }
            public string cls { get; set; }
            public string fullName { get; set; }
            public string[] bases { get; set; }
            public int order { get; set; }
            public bool hidden { get; set; }
            public string group { get; set; }
            public bool loose { get; set; }
            /// <summary>protected virtual bool UsesEntrances 的实例值；链上没有这个属性则 null。</summary>
            public bool? usesEntrances { get; set; }
            /// <summary>protected virtual bool TypedEntrances 的实例值；链上没有则 null。</summary>
            public bool? typedEntrances { get; set; }
            /// <summary>CheckReady 最近一次被声明的类型名。</summary>
            public string checkReadyBy { get; set; }
            /// <summary>SuggestMarkers 最近一次被声明的类型名。</summary>
            public string suggestBy { get; set; }
            /// <summary>LayerAlias 穿透后的 canonical id（非别名时为自身）。</summary>
            public string canonicalId { get; set; }
            public bool isAlias { get; set; }
            public float minArea { get; set; }
            public float maxArea { get; set; }
            public string[] accepts { get; set; }
            public string[] emits { get; set; }
        }

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>沿继承链找第一个**自己声明**了 method 的类型（虚方法链上最近的覆写点）。</summary>
        private static string Declarer(Type t, string method)
        {
            for (var c = t; c != null; c = c.BaseType)
                if (c.GetMethod(method, Any) != null) return c.Name;
            return null;
        }

        /// <summary>读 protected/public virtual bool 属性在**这个实例**上的值。没有该属性返回 null。</summary>
        private static bool? BoolProp(object g, string name)
        {
            for (var c = g.GetType(); c != null; c = c.BaseType)
            {
                var p = c.GetProperty(name, Any);
                if (p == null || p.PropertyType != typeof(bool) || !p.CanRead) continue;
                try { return (bool)p.GetValue(g); }
                catch { return null; }
            }
            return null;
        }

        public static int Run(string outPath)
        {
            var all = GeneratorRegistry.All;
            var rows = new List<Row>(all.Count);

            foreach (var g in all)
            {
                var t = g.GetType();
                var attr = (AreaGeneratorAttribute)Attribute.GetCustomAttribute(t, typeof(AreaGeneratorAttribute));
                var canonical = LayerAliases.Canonical(g.Id);

                var bases = new List<string>();
                for (var c = t; c != null && c != typeof(object); c = c.BaseType) bases.Add(c.Name);

                rows.Add(new Row
                {
                    id = g.Id,
                    kind = g.Kind.ToString(),
                    cls = t.Name,
                    fullName = t.FullName,
                    bases = bases.ToArray(),
                    order = attr?.Order ?? -1,
                    hidden = attr?.Hidden ?? false,
                    group = attr?.Group ?? "",
                    loose = attr?.Loose ?? false,
                    usesEntrances = BoolProp(g, "UsesEntrances"),
                    typedEntrances = BoolProp(g, "TypedEntrances"),
                    checkReadyBy = Declarer(t, "CheckReady"),
                    suggestBy = Declarer(t, "SuggestMarkers"),
                    canonicalId = canonical,
                    isAlias = canonical != g.Id,
                    minArea = g.MinArea,
                    maxArea = g.MaxArea,
                    accepts = g.Accepts ?? Array.Empty<string>(),
                    emits = g.Emits ?? Array.Empty<string>(),
                });
            }

            var doc = new
            {
                protocol = "placemaker-generators/1",
                note = "反射自 GeneratorRegistry.All。只陈述事实（虚方法声明点 + 开关实例值），family 归类在 TS 侧。",
                count = rows.Count,
                generators = rows.OrderBy(r => r.order).ThenBy(r => r.id, StringComparer.Ordinal).ToArray(),
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(outPath, json, new UTF8Encoding(false));

            Console.WriteLine("[generators] " + rows.Count + " 个生成器 → " + outPath);

            // 控制台给一张分布表，免得每次都要去翻 JSON
            var byDecl = rows
                .GroupBy(r => r.checkReadyBy ?? "(none)")
                .OrderByDescending(x => x.Count());
            Console.WriteLine("[generators] CheckReady 声明点分布：");
            foreach (var g in byDecl)
                Console.WriteLine("    " + g.Key.PadRight(22) + g.Count());
            Console.WriteLine("[generators] 其中别名（LayerAlias 穿透）：" +
                              rows.Count(r => r.isAlias) + "  隐藏：" + rows.Count(r => r.hidden));
            return 0;
        }
    }
}
