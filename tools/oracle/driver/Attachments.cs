// 贴合点导出器：把 Core 的 Tool/AttachmentFinder.Find 跑一遍，输出 JSON。
//
// 为什么单独做：TS 侧把 AttachmentFinder 移植了一遍，但"移植得像不像"不能靠读代码。
// 这个模式让**真 C# 实现**成为对照基准——同一份 ring + streets，两边各算一次，
// 逐点比对（kind / ringSegment / 位置）。这正是 docs/A R3 的落地方式。
//
// 用法：PlacemakerDriver --attachments <case.json> <attachments.json>
// 输入格式与 --emit 一致（shape.ring + streets[]），只读这两项。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Unity.Mathematics;
using Placemaker.Core;
using Placemaker.Core.Tool;

namespace Placemaker.Driver
{
    internal static class Attachments
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        public static int Run(string casePath, string outPath)
        {
            if (!File.Exists(casePath))
            {
                Console.Error.WriteLine("[attachments] 找不到输入文件: " + casePath);
                return 2;
            }

            var ring = new List<float3>();
            var nets = new List<NearbyNet>();

            using (var doc = JsonDocument.Parse(File.ReadAllText(casePath, Encoding.UTF8)))
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("shape", out var shape) && shape.TryGetProperty("ring", out var r))
                    foreach (var p in r.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                            ring.Add(new float3(p[0].GetSingle(), 0f, p[1].GetSingle()));

                if (root.TryGetProperty("streets", out var st) && st.ValueKind == JsonValueKind.Array)
                    foreach (var s in st.EnumerateArray())
                        nets.Add(ReadStreet(s));
            }

            if (ring.Count < 3)
            {
                Console.Error.WriteLine("[attachments] ring 少于 3 点");
                return 2;
            }

            var list = AttachmentFinder.Find(ring.ToArray(), nets);

            var sb = new StringBuilder();
            sb.Append("{\"protocol\":\"placemaker-attachments/1\",\"ringPoints\":").Append(ring.Count);
            sb.Append(",\"nets\":").Append(nets.Count);
            sb.Append(",\"count\":").Append(list.Count);
            sb.Append(",\"attachments\":[");
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"kind\":\"").Append(KindName(a.Kind)).Append('"');
                sb.Append(",\"externalId\":").Append(a.ExternalId.ToString(INV));
                sb.Append(",\"externalT\":").Append(F(a.ExternalT));
                sb.Append(",\"position\":[").Append(F(a.Position.x)).Append(',').Append(F(a.Position.z)).Append(']');
                // roadNode 没有方向；这里照样输出 0 向量，消费方按 kind 判空
                sb.Append(",\"direction\":[").Append(F(a.Direction.x)).Append(',').Append(F(a.Direction.z)).Append(']');
                sb.Append(",\"roadWidth\":").Append(F(a.RoadWidth));
                sb.Append(",\"ringSegment\":").Append(a.RingSegment.ToString(INV));
                sb.Append(",\"ringT\":").Append(F(a.RingT));
                sb.Append('}');
            }
            sb.Append("]}");

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

            int along = 0, cross = 0, node = 0;
            foreach (var a in list)
            {
                if (a.Kind == AttachmentKind.RoadAlongEdge) along++;
                else if (a.Kind == AttachmentKind.RoadCrossing) cross++;
                else node++;
            }
            Console.WriteLine("[attachments] 沿边 " + along + "  穿越 " + cross + "  节点 " + node + "  → " + outPath);
            return 0;
        }

        private static string KindName(AttachmentKind k) =>
            k == AttachmentKind.RoadCrossing ? "roadCrossing"
            : k == AttachmentKind.RoadNode ? "roadNode"
            : "roadAlongEdge";

        private static string F(float v) => v.ToString("0.####", INV);

        /// <summary>与 Emit.ReadStreet 保持同一套默认值，否则两边输入都不一致就没法比。</summary>
        private static NearbyNet ReadStreet(JsonElement s)
        {
            var a = Vec2(s, "a") ?? new float2(-20f, -8f);
            var b = Vec2(s, "b") ?? new float2(20f, -8f);
            var A = new float3(a.x, 0f, a.y);
            var D = new float3(b.x, 0f, b.y);
            var cat = Str(s, "category", "road");
            return new NearbyNet
            {
                ExternalId = (long)Num(s, "id", 1),
                A = A,
                B = A + (D - A) / 3f,
                C = A + (D - A) * (2f / 3f),
                D = D,
                Width = (float)Num(s, "width", 8f),
                Category = cat == "pathway" ? NetCategory.Pathway
                         : cat == "track" ? NetCategory.Track
                         : cat == "other" ? NetCategory.Other
                         : NetCategory.Road,
                HasSidewalk = Bool(s, "sidewalk", true),
                IsOneWay = Bool(s, "oneWay", false),
                IsHighway = Bool(s, "highway", false),
                SpeedLimit = (float)Num(s, "speed", 50f),
            };
        }

        private static float2? Vec2(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2) return null;
            return new float2(v[0].GetSingle(), v[1].GetSingle());
        }

        private static string Str(JsonElement e, string name, string fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

        private static double Num(JsonElement e, string name, double fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

        private static bool Bool(JsonElement e, string name, bool fallback) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
                ? v.GetBoolean() : fallback;
    }
}
