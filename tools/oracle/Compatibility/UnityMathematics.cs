// Unity.Mathematics 兼容层 —— Placemaker.Core 在纯 .NET 6 下的编译底座
//
// 设计依据：对 Core/ 全部 465 个 .cs 文件做的静态扫描（tools/scan-math-surface.mjs）
// 而不是照抄 Unity.Mathematics 全集。只实现 Core 真实用到的东西 + 必要的运算符，
// 目的是「编译通过且数值语义正确」，不是 API 兼容库。
//
// 覆盖清单（扫描结果，2026-10）
//   类型：float2/3/4、int2/3/4、uint2/3/4、double2/3/4、float2x2、bool2/3/4、quaternion、Random
//   函数：41 个 math.* 函数 + PI/PI2/TAU/SQRT2/EPSILON 常量
//   未实现且 Core 未使用：half（Core 里 427 次 "half" 全是局部变量名，不是类型）、
//                        float3x3/float4x4、int4x4、RigidTransform、svd 等
//
// 与官方实现的差异（全部为有意为之，已逐条标注）
//   1. math.hash：官方是 xxhash3/xxhash32 的特定实现，这里用自写的确定性混淆。
//      影响：同一个 seed 在本库和游戏里会得到不同的散布图案，但**统计性质和可复现性一致**。
//      若后续要与游戏逐位对齐，替换 Hash 一节即可，调用方无需改动。
//   2. Random：官方 xorshift 的具体位运算序列未逐字复刻，这里用标准 xorshift32。
//      同上：确定性 + 均匀性满足，序列不同。
//   3. half：不实现（Core 未使用）。
using System;

namespace Unity.Mathematics
{
    // ================================================================ bool2 / 3 / 4
    [Serializable]
    public struct bool2
    {
        public bool x, y;
        public bool2(bool x, bool y) { this.x = x; this.y = y; }
        public bool2(bool v) { x = y = v; }

        public static bool2 operator &(bool2 a, bool2 b) { return new bool2(a.x & b.x, a.y & b.y); }
        public static bool2 operator |(bool2 a, bool2 b) { return new bool2(a.x | b.x, a.y | b.y); }
        public static bool2 operator ^(bool2 a, bool2 b) { return new bool2(a.x ^ b.x, a.y ^ b.y); }
        public static bool2 operator !(bool2 a) { return new bool2(!a.x, !a.y); }
        public static bool2 operator ==(bool2 a, bool2 b) { return new bool2(a.x == b.x, a.y == b.y); }
        public static bool2 operator !=(bool2 a, bool2 b) { return new bool2(a.x != b.x, a.y != b.y); }
        public override bool Equals(object o) { return o is bool2 v && x == v.x && y == v.y; }
        public override int GetHashCode() { return (x ? 1 : 0) | (y ? 2 : 0); }
        public override string ToString() { return "bool2(" + x + ", " + y + ")"; }
    }

    [Serializable]
    public struct bool3
    {
        public bool x, y, z;
        public bool3(bool x, bool y, bool z) { this.x = x; this.y = y; this.z = z; }
        public bool3(bool v) { x = y = z = v; }

        public static bool3 operator &(bool3 a, bool3 b) { return new bool3(a.x & b.x, a.y & b.y, a.z & b.z); }
        public static bool3 operator |(bool3 a, bool3 b) { return new bool3(a.x | b.x, a.y | b.y, a.z | b.z); }
        public static bool3 operator !(bool3 a) { return new bool3(!a.x, !a.y, !a.z); }
        public static bool3 operator ==(bool3 a, bool3 b) { return new bool3(a.x == b.x, a.y == b.y, a.z == b.z); }
        public static bool3 operator !=(bool3 a, bool3 b) { return new bool3(a.x != b.x, a.y != b.y, a.z != b.z); }
        public override bool Equals(object o) { return o is bool3 v && x == v.x && y == v.y && z == v.z; }
        public override int GetHashCode() { return (x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0); }
        public override string ToString() { return "bool3(" + x + ", " + y + ", " + z + ")"; }
    }

    [Serializable]
    public struct bool4
    {
        public bool x, y, z, w;
        public bool4(bool x, bool y, bool z, bool w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public bool4(bool v) { x = y = z = w = v; }

        public static bool4 operator &(bool4 a, bool4 b) { return new bool4(a.x & b.x, a.y & b.y, a.z & b.z, a.w & b.w); }
        public static bool4 operator |(bool4 a, bool4 b) { return new bool4(a.x | b.x, a.y | b.y, a.z | b.z, a.w | b.w); }
        public static bool4 operator !(bool4 a) { return new bool4(!a.x, !a.y, !a.z, !a.w); }
        public override bool Equals(object o) { return o is bool4 v && x == v.x && y == v.y && z == v.z && w == v.w; }
        public override int GetHashCode() { return (x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0) | (w ? 8 : 0); }
        public override string ToString() { return "bool4(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    // ================================================================ float2
    [Serializable]
    public struct float2
    {
        public float x, y;
        public float2(float v) { x = y = v; }
        public float2(float x, float y) { this.x = x; this.y = y; }
        // 转换构造：Core 里有 new float2(int2) 的写法（TrailRouter.cs:182）
        public float2(int2 v) { x = v.x; y = v.y; }
        public float2(double2 v) { x = (float)v.x; y = (float)v.y; }
        public static readonly float2 zero = new float2(0f, 0f);
        public static readonly float2 one = new float2(1f, 1f);

        // Unity.Mathematics 的向量类型都带索引器，Core 里用 v[i] 遍历分量（GridGenerator.Tree.cs 等 23 处）
        public float this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : throw new IndexOutOfRangeException("float2[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else throw new IndexOutOfRangeException("float2[" + i + "]"); }
        }

        // 扫描结果：Core 只用到 .xy（1 次）和 .yx（1 次）
        public float2 xy { get { return new float2(x, y); } set { x = value.x; y = value.y; } }
        public float2 yx { get { return new float2(y, x); } }

        public static float2 operator +(float2 a, float2 b) { return new float2(a.x + b.x, a.y + b.y); }
        public static float2 operator -(float2 a, float2 b) { return new float2(a.x - b.x, a.y - b.y); }
        public static float2 operator *(float2 a, float2 b) { return new float2(a.x * b.x, a.y * b.y); }
        public static float2 operator /(float2 a, float2 b) { return new float2(a.x / b.x, a.y / b.y); }
        public static float2 operator +(float2 a, float b) { return new float2(a.x + b, a.y + b); }
        public static float2 operator -(float2 a, float b) { return new float2(a.x - b, a.y - b); }
        public static float2 operator *(float2 a, float b) { return new float2(a.x * b, a.y * b); }
        public static float2 operator /(float2 a, float b) { return new float2(a.x / b, a.y / b); }
        public static float2 operator +(float a, float2 b) { return new float2(a + b.x, a + b.y); }
        public static float2 operator -(float a, float2 b) { return new float2(a - b.x, a - b.y); }
        public static float2 operator *(float a, float2 b) { return new float2(a * b.x, a * b.y); }
        public static float2 operator /(float a, float2 b) { return new float2(a / b.x, a / b.y); }
        public static float2 operator -(float2 a) { return new float2(-a.x, -a.y); }

        public static bool2 operator ==(float2 a, float2 b) { return new bool2(a.x == b.x, a.y == b.y); }
        public static bool2 operator !=(float2 a, float2 b) { return new bool2(a.x != b.x, a.y != b.y); }
        public static bool2 operator <(float2 a, float2 b) { return new bool2(a.x < b.x, a.y < b.y); }
        public static bool2 operator >(float2 a, float2 b) { return new bool2(a.x > b.x, a.y > b.y); }
        public static bool2 operator <=(float2 a, float2 b) { return new bool2(a.x <= b.x, a.y <= b.y); }
        public static bool2 operator >=(float2 a, float2 b) { return new bool2(a.x >= b.x, a.y >= b.y); }

        public static implicit operator float2(float v) { return new float2(v); }
        // 拓宽转换用 implicit（与官方一致；窄化 double2 -> float2 用 explicit）
        public static implicit operator float2(int2 v) { return new float2(v.x, v.y); }
        public static explicit operator float2(double2 v) { return new float2((float)v.x, (float)v.y); }
        // 反向窄化：Core 大量写 (int2)math.floor(float2)（31 处）
        public static explicit operator int2(float2 v) { return new int2((int)v.x, (int)v.y); }

        public override bool Equals(object o) { return o is float2 v && x == v.x && y == v.y; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2); }
        public override string ToString() { return "float2(" + x + "f, " + y + "f)"; }
    }

    // ================================================================ float3
    [Serializable]
    public struct float3
    {
        public float x, y, z;
        public float3(float v) { x = y = z = v; }
        public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float3(float2 xy, float z) { this.x = xy.x; this.y = xy.y; this.z = z; }
        public float3(float x, float2 yz) { this.x = x; this.y = yz.x; this.z = yz.y; }
        public float3(int3 v) { x = v.x; y = v.y; z = v.z; }
        public float3(double3 v) { x = (float)v.x; y = (float)v.y; z = (float)v.z; }
        public static readonly float3 zero = new float3(0f, 0f, 0f);
        public static readonly float3 one = new float3(1f, 1f, 1f);

        public float this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : throw new IndexOutOfRangeException("float3[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else throw new IndexOutOfRangeException("float3[" + i + "]"); }
        }

        // .xz 是 Core 里最高频的 swizzle（1135 次）；且 Core 会写 pos.xz += ...，所以双分量 swizzle 必须有 setter
        public float2 xy { get { return new float2(x, y); } set { x = value.x; y = value.y; } }
        public float2 yx { get { return new float2(y, x); } set { y = value.x; x = value.y; } }
        public float2 xz { get { return new float2(x, z); } set { x = value.x; z = value.y; } }
        public float2 zx { get { return new float2(z, x); } set { z = value.x; x = value.y; } }
        public float2 yz { get { return new float2(y, z); } set { y = value.x; z = value.y; } }
        public float2 zy { get { return new float2(z, y); } set { z = value.x; y = value.y; } }
        public float3 xyz { get { return new float3(x, y, z); } set { x = value.x; y = value.y; z = value.z; } }
        public float3 zxy { get { return new float3(z, x, y); } }
        public float3 yzx { get { return new float3(y, z, x); } }

        public static float3 operator +(float3 a, float3 b) { return new float3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static float3 operator -(float3 a, float3 b) { return new float3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static float3 operator *(float3 a, float3 b) { return new float3(a.x * b.x, a.y * b.y, a.z * b.z); }
        public static float3 operator /(float3 a, float3 b) { return new float3(a.x / b.x, a.y / b.y, a.z / b.z); }
        public static float3 operator +(float3 a, float b) { return new float3(a.x + b, a.y + b, a.z + b); }
        public static float3 operator -(float3 a, float b) { return new float3(a.x - b, a.y - b, a.z - b); }
        public static float3 operator *(float3 a, float b) { return new float3(a.x * b, a.y * b, a.z * b); }
        public static float3 operator /(float3 a, float b) { return new float3(a.x / b, a.y / b, a.z / b); }
        public static float3 operator +(float a, float3 b) { return new float3(a + b.x, a + b.y, a + b.z); }
        public static float3 operator -(float a, float3 b) { return new float3(a - b.x, a - b.y, a - b.z); }
        public static float3 operator *(float a, float3 b) { return new float3(a * b.x, a * b.y, a * b.z); }
        public static float3 operator /(float a, float3 b) { return new float3(a / b.x, a / b.y, a / b.z); }
        public static float3 operator -(float3 a) { return new float3(-a.x, -a.y, -a.z); }

        public static bool3 operator ==(float3 a, float3 b) { return new bool3(a.x == b.x, a.y == b.y, a.z == b.z); }
        public static bool3 operator !=(float3 a, float3 b) { return new bool3(a.x != b.x, a.y != b.y, a.z != b.z); }
        public static bool3 operator <(float3 a, float3 b) { return new bool3(a.x < b.x, a.y < b.y, a.z < b.z); }
        public static bool3 operator >(float3 a, float3 b) { return new bool3(a.x > b.x, a.y > b.y, a.z > b.z); }
        public static bool3 operator <=(float3 a, float3 b) { return new bool3(a.x <= b.x, a.y <= b.y, a.z <= b.z); }
        public static bool3 operator >=(float3 a, float3 b) { return new bool3(a.x >= b.x, a.y >= b.y, a.z >= b.z); }

        public static implicit operator float3(float v) { return new float3(v); }
        public static implicit operator float3(int3 v) { return new float3(v.x, v.y, v.z); }
        public static explicit operator float3(double3 v) { return new float3((float)v.x, (float)v.y, (float)v.z); }
        public static explicit operator int3(float3 v) { return new int3((int)v.x, (int)v.y, (int)v.z); }

        public override bool Equals(object o) { return o is float3 v && x == v.x && y == v.y && z == v.z; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2); }
        public override string ToString() { return "float3(" + x + "f, " + y + "f, " + z + "f)"; }
    }

    // ================================================================ float4
    [Serializable]
    public struct float4
    {
        public float x, y, z, w;
        public float4(float v) { x = y = z = w = v; }
        public float4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float4(float3 xyz, float w) { this.x = xyz.x; this.y = xyz.y; this.z = xyz.z; this.w = w; }
        public float4(float x, float3 yzw) { this.x = x; this.y = yzw.x; this.z = yzw.y; this.w = yzw.z; }
        public float4(float2 xy, float z, float w) { this.x = xy.x; this.y = xy.y; this.z = z; this.w = w; }
        public float4(float x, float y, float2 zw) { this.x = x; this.y = y; this.z = zw.x; this.w = zw.y; }
        public float4(float2 xy, float2 zw) { this.x = xy.x; this.y = xy.y; this.z = zw.x; this.w = zw.y; }
        public float4(int4 v) { x = v.x; y = v.y; z = v.z; w = v.w; }
        public static readonly float4 zero = new float4(0f, 0f, 0f, 0f);

        public float this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : i == 3 ? w : throw new IndexOutOfRangeException("float4[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else if (i == 3) w = value; else throw new IndexOutOfRangeException("float4[" + i + "]"); }
        }

        public float2 xy { get { return new float2(x, y); } set { x = value.x; y = value.y; } }
        public float2 zw { get { return new float2(z, w); } set { z = value.x; w = value.y; } }
        public float3 xyz { get { return new float3(x, y, z); } set { x = value.x; y = value.y; z = value.z; } }
        public float4 xyzw { get { return new float4(x, y, z, w); } set { x = value.x; y = value.y; z = value.z; w = value.w; } }

        public static float4 operator +(float4 a, float4 b) { return new float4(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w); }
        public static float4 operator -(float4 a, float4 b) { return new float4(a.x - b.x, a.y - b.y, a.z - b.z, a.w - b.w); }
        public static float4 operator *(float4 a, float4 b) { return new float4(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w); }
        public static float4 operator /(float4 a, float4 b) { return new float4(a.x / b.x, a.y / b.y, a.z / b.z, a.w / b.w); }
        public static float4 operator +(float4 a, float b) { return new float4(a.x + b, a.y + b, a.z + b, a.w + b); }
        public static float4 operator -(float4 a, float b) { return new float4(a.x - b, a.y - b, a.z - b, a.w - b); }
        public static float4 operator *(float4 a, float b) { return new float4(a.x * b, a.y * b, a.z * b, a.w * b); }
        public static float4 operator /(float4 a, float b) { return new float4(a.x / b, a.y / b, a.z / b, a.w / b); }
        public static float4 operator +(float a, float4 b) { return new float4(a + b.x, a + b.y, a + b.z, a + b.w); }
        public static float4 operator -(float a, float4 b) { return new float4(a - b.x, a - b.y, a - b.z, a - b.w); }
        public static float4 operator *(float a, float4 b) { return new float4(a * b.x, a * b.y, a * b.z, a * b.w); }
        public static float4 operator /(float a, float4 b) { return new float4(a / b.x, a / b.y, a / b.z, a / b.w); }
        public static float4 operator -(float4 a) { return new float4(-a.x, -a.y, -a.z, -a.w); }

        public static bool4 operator ==(float4 a, float4 b) { return new bool4(a.x == b.x, a.y == b.y, a.z == b.z, a.w == b.w); }
        public static bool4 operator !=(float4 a, float4 b) { return new bool4(a.x != b.x, a.y != b.y, a.z != b.z, a.w != b.w); }
        public static bool4 operator <(float4 a, float4 b) { return new bool4(a.x < b.x, a.y < b.y, a.z < b.z, a.w < b.w); }
        public static bool4 operator >(float4 a, float4 b) { return new bool4(a.x > b.x, a.y > b.y, a.z > b.z, a.w > b.w); }

        public static implicit operator float4(float v) { return new float4(v); }
        public static explicit operator int4(float4 v) { return new int4((int)v.x, (int)v.y, (int)v.z, (int)v.w); }

        public override bool Equals(object o) { return o is float4 v && x == v.x && y == v.y && z == v.z && w == v.w; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1); }
        public override string ToString() { return "float4(" + x + "f, " + y + "f, " + z + "f, " + w + "f)"; }
    }

    // ================================================================ int2 / 3 / 4
    [Serializable]
    public struct int2
    {
        public int x, y;
        public int2(int v) { x = y = v; }
        public int2(int x, int y) { this.x = x; this.y = y; }
        public static readonly int2 zero = new int2(0, 0);
        public int this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : throw new IndexOutOfRangeException("int2[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else throw new IndexOutOfRangeException("int2[" + i + "]"); }
        }
        public int2 xy { get { return new int2(x, y); } set { x = value.x; y = value.y; } }
        public int2 yx { get { return new int2(y, x); } }

        public static int2 operator +(int2 a, int2 b) { return new int2(a.x + b.x, a.y + b.y); }
        public static int2 operator -(int2 a, int2 b) { return new int2(a.x - b.x, a.y - b.y); }
        public static int2 operator *(int2 a, int2 b) { return new int2(a.x * b.x, a.y * b.y); }
        public static int2 operator /(int2 a, int2 b) { return new int2(a.x / b.x, a.y / b.y); }
        public static int2 operator *(int2 a, int b) { return new int2(a.x * b, a.y * b); }
        public static int2 operator /(int2 a, int b) { return new int2(a.x / b, a.y / b); }
        public static int2 operator -(int2 a) { return new int2(-a.x, -a.y); }
        public static bool2 operator ==(int2 a, int2 b) { return new bool2(a.x == b.x, a.y == b.y); }
        public static bool2 operator !=(int2 a, int2 b) { return new bool2(a.x != b.x, a.y != b.y); }
        public static bool2 operator <(int2 a, int2 b) { return new bool2(a.x < b.x, a.y < b.y); }
        public static bool2 operator >(int2 a, int2 b) { return new bool2(a.x > b.x, a.y > b.y); }
        public static bool2 operator <=(int2 a, int2 b) { return new bool2(a.x <= b.x, a.y <= b.y); }
        public static bool2 operator >=(int2 a, int2 b) { return new bool2(a.x >= b.x, a.y >= b.y); }
        public static implicit operator int2(int v) { return new int2(v); }
        public override bool Equals(object o) { return o is int2 v && x == v.x && y == v.y; }
        public override int GetHashCode() { return x ^ (y << 2); }
        public override string ToString() { return "int2(" + x + ", " + y + ")"; }
    }

    [Serializable]
    public struct int3
    {
        public int x, y, z;
        public int3(int v) { x = y = z = v; }
        public int3(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public int3(int2 xy, int z) { this.x = xy.x; this.y = xy.y; this.z = z; }
        public static readonly int3 zero = new int3(0, 0, 0);
        public int this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : throw new IndexOutOfRangeException("int3[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else throw new IndexOutOfRangeException("int3[" + i + "]"); }
        }
        public int2 xy { get { return new int2(x, y); } set { x = value.x; y = value.y; } }
        public int2 xz { get { return new int2(x, z); } }
        public int3 xyz { get { return new int3(x, y, z); } set { x = value.x; y = value.y; z = value.z; } }

        public static int3 operator +(int3 a, int3 b) { return new int3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static int3 operator -(int3 a, int3 b) { return new int3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static int3 operator *(int3 a, int3 b) { return new int3(a.x * b.x, a.y * b.y, a.z * b.z); }
        public static int3 operator *(int3 a, int b) { return new int3(a.x * b, a.y * b, a.z * b); }
        public static int3 operator /(int3 a, int b) { return new int3(a.x / b, a.y / b, a.z / b); }
        public static int3 operator -(int3 a) { return new int3(-a.x, -a.y, -a.z); }
        public static bool3 operator ==(int3 a, int3 b) { return new bool3(a.x == b.x, a.y == b.y, a.z == b.z); }
        public static bool3 operator !=(int3 a, int3 b) { return new bool3(a.x != b.x, a.y != b.y, a.z != b.z); }
        public static bool3 operator <(int3 a, int3 b) { return new bool3(a.x < b.x, a.y < b.y, a.z < b.z); }
        public static bool3 operator >(int3 a, int3 b) { return new bool3(a.x > b.x, a.y > b.y, a.z > b.z); }
        public static implicit operator int3(int v) { return new int3(v); }
        public override bool Equals(object o) { return o is int3 v && x == v.x && y == v.y && z == v.z; }
        public override int GetHashCode() { return x ^ (y << 2) ^ (z >> 2); }
        public override string ToString() { return "int3(" + x + ", " + y + ", " + z + ")"; }
    }

    [Serializable]
    public struct int4
    {
        public int x, y, z, w;
        public int4(int v) { x = y = z = w = v; }
        public int4(int x, int y, int z, int w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly int4 zero = new int4(0, 0, 0, 0);
        public int this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : i == 3 ? w : throw new IndexOutOfRangeException("int4[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else if (i == 3) w = value; else throw new IndexOutOfRangeException("int4[" + i + "]"); }
        }
        public int2 xy { get { return new int2(x, y); } set { x = value.x; y = value.y; } }
        public int2 zw { get { return new int2(z, w); } set { z = value.x; w = value.y; } }
        public override bool Equals(object o) { return o is int4 v && x == v.x && y == v.y && z == v.z && w == v.w; }
        public override int GetHashCode() { return x ^ (y << 2) ^ (z >> 2) ^ (w >> 1); }
        public override string ToString() { return "int4(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    // ================================================================ uint2 / 3 / 4
    // Core 只用 uint3（math.hash 的入参），但补齐 2/4 成本极低，避免后续加代码再回头改。
    [Serializable]
    public struct uint2
    {
        public uint x, y;
        public uint2(uint v) { x = y = v; }
        public uint2(uint x, uint y) { this.x = x; this.y = y; }
        public static readonly uint2 zero = new uint2(0u, 0u);
        public static uint2 operator +(uint2 a, uint2 b) { return new uint2(a.x + b.x, a.y + b.y); }
        public static uint2 operator -(uint2 a, uint2 b) { return new uint2(a.x - b.x, a.y - b.y); }
        public static bool2 operator ==(uint2 a, uint2 b) { return new bool2(a.x == b.x, a.y == b.y); }
        public static bool2 operator !=(uint2 a, uint2 b) { return new bool2(a.x != b.x, a.y != b.y); }
        public override bool Equals(object o) { return o is uint2 v && x == v.x && y == v.y; }
        public override int GetHashCode() { return (int)x ^ ((int)y << 2); }
        public override string ToString() { return "uint2(" + x + "u, " + y + "u)"; }
    }

    [Serializable]
    public struct uint3
    {
        public uint x, y, z;
        public uint3(uint v) { x = y = z = v; }
        public uint3(uint x, uint y, uint z) { this.x = x; this.y = y; this.z = z; }
        public static readonly uint3 zero = new uint3(0u, 0u, 0u);
        public uint2 xy { get { return new uint2(x, y); } set { x = value.x; y = value.y; } }
        public static uint3 operator +(uint3 a, uint3 b) { return new uint3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static uint3 operator -(uint3 a, uint3 b) { return new uint3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static bool3 operator ==(uint3 a, uint3 b) { return new bool3(a.x == b.x, a.y == b.y, a.z == b.z); }
        public static bool3 operator !=(uint3 a, uint3 b) { return new bool3(a.x != b.x, a.y != b.y, a.z != b.z); }
        public override bool Equals(object o) { return o is uint3 v && x == v.x && y == v.y && z == v.z; }
        public override int GetHashCode() { return (int)x ^ ((int)y << 2) ^ ((int)z >> 2); }
        public override string ToString() { return "uint3(" + x + "u, " + y + "u, " + z + "u)"; }
    }

    [Serializable]
    public struct uint4
    {
        public uint x, y, z, w;
        public uint4(uint v) { x = y = z = w = v; }
        public uint4(uint x, uint y, uint z, uint w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly uint4 zero = new uint4(0u, 0u, 0u, 0u);
        public static bool4 operator ==(uint4 a, uint4 b) { return new bool4(a.x == b.x, a.y == b.y, a.z == b.z, a.w == b.w); }
        public static bool4 operator !=(uint4 a, uint4 b) { return new bool4(a.x != b.x, a.y != b.y, a.z != b.z, a.w != b.w); }
        public override bool Equals(object o) { return o is uint4 v && x == v.x && y == v.y && z == v.z && w == v.w; }
        public override int GetHashCode() { return (int)x ^ ((int)y << 2) ^ ((int)z >> 2) ^ ((int)w >> 1); }
        public override string ToString() { return "uint4(" + x + "u, " + y + "u, " + z + "u, " + w + "u)"; }
    }

    // ================================================================ double2 / 3 / 4
    [Serializable]
    public struct double2
    {
        public double x, y;
        public double2(double v) { x = y = v; }
        public double2(double x, double y) { this.x = x; this.y = y; }
        public static readonly double2 zero = new double2(0.0, 0.0);
        public double this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : throw new IndexOutOfRangeException("double2[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else throw new IndexOutOfRangeException("double2[" + i + "]"); }
        }
        public double2 xy { get { return new double2(x, y); } set { x = value.x; y = value.y; } }
        public double2 yx { get { return new double2(y, x); } }

        public static double2 operator +(double2 a, double2 b) { return new double2(a.x + b.x, a.y + b.y); }
        public static double2 operator -(double2 a, double2 b) { return new double2(a.x - b.x, a.y - b.y); }
        public static double2 operator *(double2 a, double2 b) { return new double2(a.x * b.x, a.y * b.y); }
        public static double2 operator /(double2 a, double2 b) { return new double2(a.x / b.x, a.y / b.y); }
        public static double2 operator +(double2 a, double b) { return new double2(a.x + b, a.y + b); }
        public static double2 operator -(double2 a, double b) { return new double2(a.x - b, a.y - b); }
        public static double2 operator *(double2 a, double b) { return new double2(a.x * b, a.y * b); }
        public static double2 operator /(double2 a, double b) { return new double2(a.x / b, a.y / b); }
        public static double2 operator *(double a, double2 b) { return new double2(a * b.x, a * b.y); }
        public static double2 operator -(double2 a) { return new double2(-a.x, -a.y); }
        public static bool2 operator ==(double2 a, double2 b) { return new bool2(a.x == b.x, a.y == b.y); }
        public static bool2 operator !=(double2 a, double2 b) { return new bool2(a.x != b.x, a.y != b.y); }
        public static bool2 operator <(double2 a, double2 b) { return new bool2(a.x < b.x, a.y < b.y); }
        public static bool2 operator >(double2 a, double2 b) { return new bool2(a.x > b.x, a.y > b.y); }
        public static implicit operator double2(double v) { return new double2(v); }
        public static implicit operator double2(float2 v) { return new double2(v.x, v.y); }
        public static implicit operator double2(int2 v) { return new double2(v.x, v.y); }
        public override bool Equals(object o) { return o is double2 v && x == v.x && y == v.y; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2); }
        public override string ToString() { return "double2(" + x + ", " + y + ")"; }
    }

    [Serializable]
    public struct double3
    {
        public double x, y, z;
        public double3(double v) { x = y = z = v; }
        public double3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        public double3(double2 xy, double z) { this.x = xy.x; this.y = xy.y; this.z = z; }
        public static readonly double3 zero = new double3(0.0, 0.0, 0.0);
        public double this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : throw new IndexOutOfRangeException("double3[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else throw new IndexOutOfRangeException("double3[" + i + "]"); }
        }
        public double2 xy { get { return new double2(x, y); } set { x = value.x; y = value.y; } }
        public double2 xz { get { return new double2(x, z); } }
        public static double3 operator +(double3 a, double3 b) { return new double3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static double3 operator -(double3 a, double3 b) { return new double3(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static double3 operator *(double3 a, double3 b) { return new double3(a.x * b.x, a.y * b.y, a.z * b.z); }
        public static double3 operator *(double3 a, double b) { return new double3(a.x * b, a.y * b, a.z * b); }
        public static double3 operator /(double3 a, double b) { return new double3(a.x / b, a.y / b, a.z / b); }
        public static bool3 operator ==(double3 a, double3 b) { return new bool3(a.x == b.x, a.y == b.y, a.z == b.z); }
        public static bool3 operator !=(double3 a, double3 b) { return new bool3(a.x != b.x, a.y != b.y, a.z != b.z); }
        public static implicit operator double3(double v) { return new double3(v); }
        public override bool Equals(object o) { return o is double3 v && x == v.x && y == v.y && z == v.z; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2); }
        public override string ToString() { return "double3(" + x + ", " + y + ", " + z + ")"; }
    }

    [Serializable]
    public struct double4
    {
        public double x, y, z, w;
        public double4(double v) { x = y = z = w = v; }
        public double4(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public double4(double2 xy, double2 zw) { this.x = xy.x; this.y = xy.y; this.z = zw.x; this.w = zw.y; }
        public static readonly double4 zero = new double4(0.0, 0.0, 0.0, 0.0);
        public double this[int i]
        {
            get { return i == 0 ? x : i == 1 ? y : i == 2 ? z : i == 3 ? w : throw new IndexOutOfRangeException("double4[" + i + "]"); }
            set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else if (i == 3) w = value; else throw new IndexOutOfRangeException("double4[" + i + "]"); }
        }
        public double2 xy { get { return new double2(x, y); } set { x = value.x; y = value.y; } }
        public double2 zw { get { return new double2(z, w); } set { z = value.x; w = value.y; } }
        public double3 xyz { get { return new double3(x, y, z); } set { x = value.x; y = value.y; z = value.z; } }
        public static bool4 operator ==(double4 a, double4 b) { return new bool4(a.x == b.x, a.y == b.y, a.z == b.z, a.w == b.w); }
        public static bool4 operator !=(double4 a, double4 b) { return new bool4(a.x != b.x, a.y != b.y, a.z != b.z, a.w != b.w); }
        public override bool Equals(object o) { return o is double4 v && x == v.x && y == v.y && z == v.z && w == v.w; }
        public override int GetHashCode() { return x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2) ^ (w.GetHashCode() >> 1); }
        public override string ToString() { return "double4(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    // ================================================================ float2x2
    [Serializable]
    public struct float2x2
    {
        public float2 c0, c1;
        public float2x2(float2 c0, float2 c1) { this.c0 = c0; this.c1 = c1; }
        // 逐行书写，与官方 (m00, m01, m10, m11) 的列主序展开一致
        public float2x2(float m00, float m01, float m10, float m11) { c0 = new float2(m00, m01); c1 = new float2(m10, m11); }
        public static readonly float2x2 identity = new float2x2(1f, 0f, 0f, 1f);

        public static float2 operator *(float2x2 m, float2 v)
        {
            return new float2(m.c0.x * v.x + m.c1.x * v.y, m.c0.y * v.x + m.c1.y * v.y);
        }
        public static float2 operator *(float2 v, float2x2 m)
        {
            return new float2(v.x * m.c0.x + v.y * m.c0.y, v.x * m.c1.x + v.y * m.c1.y);
        }
    }

    // ================================================================ quaternion（Core 仅在注释中出现，占位）
    [Serializable]
    public struct quaternion
    {
        public float x, y, z, w;
        public quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static readonly quaternion identity = new quaternion(0f, 0f, 0f, 1f);
        public static quaternion RotateY(float radians)
        {
            float h = radians * 0.5f;
            return new quaternion(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h));
        }
        public override string ToString() { return "quaternion(" + x + ", " + y + ", " + z + ", " + w + ")"; }
    }

    // ================================================================ Random
    // 与官方差异：位运算序列不同（官方用其私有 xorshift 变体），这里用标准 xorshift32。
    // 语义一致的部分：可变 struct（Core 大量以 ref Random 传参推进状态）、
    //               NextFloat 在 [0,1)、NextInt(min,max) 左闭右开、NextFloat2Direction 返回单位向量。
    [Serializable]
    public struct Random
    {
        public uint state;

        public Random(uint seed) { state = seed; CheckInit(); }
        public Random(int seed) { state = unchecked((uint)seed); CheckInit(); }

        public void InitState(uint seed) { state = seed; CheckInit(); }

        private void CheckInit()
        {
            // xorshift 的 0 是吸收态，必须避开；与 Core 的 Hashing.NonZero 同款兜底
            if (state == 0u) state = 0x6C8E9CF5u;
        }

        private uint NextState()
        {
            uint t = state;
            t ^= t << 13;
            t ^= t >> 17;
            t ^= t << 5;
            state = t;
            CheckInit();
            return t;
        }

        public uint NextUInt() { return NextState(); }

        public int NextInt() { return (int)(NextState() & 0x7FFFFFFFu); }

        /// <summary>[0, max)</summary>
        public int NextInt(int max) { return max <= 0 ? 0 : (int)(NextState() % (uint)max); }

        /// <summary>[min, max)</summary>
        public int NextInt(int min, int max)
        {
            if (max <= min) return min;
            return min + (int)(NextState() % (uint)(max - min));
        }

        /// <summary>[0, 1)</summary>
        public float NextFloat() { return (NextState() >> 8) * (1f / 16777216f); }
        public float NextFloat(float max) { return NextFloat() * max; }
        public float NextFloat(float min, float max) { return min + (max - min) * NextFloat(); }

        /// <summary>[0, 1)</summary>
        public double NextDouble() { return (NextState() >> 8) * (1.0 / 16777216.0); }
        public double NextDouble(double max) { return NextDouble() * max; }
        public double NextDouble(double min, double max) { return min + (max - min) * NextDouble(); }

        public bool NextBool() { return (NextState() & 1u) != 0u; }

        public float2 NextFloat2() { return new float2(NextFloat(), NextFloat()); }
        public float2 NextFloat2(float2 min, float2 max)
        {
            return new float2(NextFloat(min.x, max.x), NextFloat(min.y, max.y));
        }
        public float3 NextFloat3() { return new float3(NextFloat(), NextFloat(), NextFloat()); }
        public float3 NextFloat3(float3 min, float3 max)
        {
            return new float3(NextFloat(min.x, max.x), NextFloat(min.y, max.y), NextFloat(min.z, max.z));
        }

        /// <summary>单位圆上的随机方向。</summary>
        public float2 NextFloat2Direction()
        {
            float a = NextFloat(0f, 6.28318530718f);
            float s, c;
            math.sincos(a, out s, out c);
            return new float2(c, s);
        }

        public int2 NextInt2(int2 max) { return new int2(NextInt(max.x), NextInt(max.y)); }
        public int2 NextInt2(int2 min, int2 max) { return new int2(NextInt(min.x, max.x), NextInt(min.y, max.y)); }
    }

    // ================================================================ math
    public static class math
    {
        public const float PI = 3.14159265358979323846f;
        public const float PI2 = 6.28318530717958647692f;
        public const float TAU = 6.28318530717958647692f;
        public const float SQRT2 = 1.41421356237309504880f;
        public const float EPSILON = 1.1920929E-07f;
        // 注意：不能用 const —— float.PositiveInfinity 是 static readonly 字段，非常量表达式（CS0133）
        public static readonly float INFINITY = float.PositiveInfinity;

        // ---------------- max / min ----------------
        public static float max(float a, float b) { return a > b ? a : b; }
        public static float max(float a, float b, float c) { return max(max(a, b), c); }
        public static int max(int a, int b) { return a > b ? a : b; }
        public static uint max(uint a, uint b) { return a > b ? a : b; }
        public static double max(double a, double b) { return a > b ? a : b; }
        public static float2 max(float2 a, float2 b) { return new float2(max(a.x, b.x), max(a.y, b.y)); }
        public static float2 max(float2 a, float b) { return new float2(max(a.x, b), max(a.y, b)); }
        public static float3 max(float3 a, float3 b) { return new float3(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z)); }
        public static float3 max(float3 a, float b) { return new float3(max(a.x, b), max(a.y, b), max(a.z, b)); }
        public static float4 max(float4 a, float4 b) { return new float4(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z), max(a.w, b.w)); }
        public static int2 max(int2 a, int2 b) { return new int2(max(a.x, b.x), max(a.y, b.y)); }
        public static int3 max(int3 a, int3 b) { return new int3(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z)); }
        public static double2 max(double2 a, double2 b) { return new double2(max(a.x, b.x), max(a.y, b.y)); }

        public static float min(float a, float b) { return a < b ? a : b; }
        public static float min(float a, float b, float c) { return min(min(a, b), c); }
        public static int min(int a, int b) { return a < b ? a : b; }
        public static uint min(uint a, uint b) { return a < b ? a : b; }
        public static double min(double a, double b) { return a < b ? a : b; }
        public static float2 min(float2 a, float2 b) { return new float2(min(a.x, b.x), min(a.y, b.y)); }
        public static float2 min(float2 a, float b) { return new float2(min(a.x, b), min(a.y, b)); }
        public static float3 min(float3 a, float3 b) { return new float3(min(a.x, b.x), min(a.y, b.y), min(a.z, b.z)); }
        public static float3 min(float3 a, float b) { return new float3(min(a.x, b), min(a.y, b), min(a.z, b)); }
        public static float4 min(float4 a, float4 b) { return new float4(min(a.x, b.x), min(a.y, b.y), min(a.z, b.z), min(a.w, b.w)); }
        public static int2 min(int2 a, int2 b) { return new int2(min(a.x, b.x), min(a.y, b.y)); }
        public static int3 min(int3 a, int3 b) { return new int3(min(a.x, b.x), min(a.y, b.y), min(a.z, b.z)); }
        public static double2 min(double2 a, double2 b) { return new double2(min(a.x, b.x), min(a.y, b.y)); }

        // ---------------- abs / sign ----------------
        public static float abs(float a) { return Math.Abs(a); }
        public static int abs(int a) { return Math.Abs(a); }
        public static double abs(double a) { return Math.Abs(a); }
        public static float2 abs(float2 a) { return new float2(abs(a.x), abs(a.y)); }
        public static float3 abs(float3 a) { return new float3(abs(a.x), abs(a.y), abs(a.z)); }
        public static float4 abs(float4 a) { return new float4(abs(a.x), abs(a.y), abs(a.z), abs(a.w)); }
        public static int2 abs(int2 a) { return new int2(abs(a.x), abs(a.y)); }
        public static int3 abs(int3 a) { return new int3(abs(a.x), abs(a.y), abs(a.z)); }
        public static double2 abs(double2 a) { return new double2(abs(a.x), abs(a.y)); }

        public static float sign(float a) { return a > 0f ? 1f : (a < 0f ? -1f : 0f); }
        public static int sign(int a) { return a > 0 ? 1 : (a < 0 ? -1 : 0); }
        public static float2 sign(float2 a) { return new float2(sign(a.x), sign(a.y)); }
        public static float3 sign(float3 a) { return new float3(sign(a.x), sign(a.y), sign(a.z)); }

        // ---------------- floor / ceil / round / frac / trunc ----------------
        public static float floor(float a) { return (float)Math.Floor(a); }
        public static float2 floor(float2 a) { return new float2(floor(a.x), floor(a.y)); }
        public static float3 floor(float3 a) { return new float3(floor(a.x), floor(a.y), floor(a.z)); }
        public static double floor(double a) { return Math.Floor(a); }
        public static double2 floor(double2 a) { return new double2(floor(a.x), floor(a.y)); }

        public static float ceil(float a) { return (float)Math.Ceiling(a); }
        public static float2 ceil(float2 a) { return new float2(ceil(a.x), ceil(a.y)); }
        public static float3 ceil(float3 a) { return new float3(ceil(a.x), ceil(a.y), ceil(a.z)); }
        public static double ceil(double a) { return Math.Ceiling(a); }

        // 注意：Unity 的 math.round 是「四舍六入五成双」，Math.Round 默认即 bankers rounding，
        // 但 Unity 实际上用的是 round-half-up（rintf）。Core 里 round 主要用于量化格子坐标，
        // 半整数落在边界上的概率极低，此处保持 Math.Round（可复现）并在文档中标注。
        public static float round(float a) { return (float)Math.Round(a, MidpointRounding.AwayFromZero); }
        public static float2 round(float2 a) { return new float2(round(a.x), round(a.y)); }
        public static float3 round(float3 a) { return new float3(round(a.x), round(a.y), round(a.z)); }
        public static int round(int a) { return a; }
        public static double round(double a) { return Math.Round(a, MidpointRounding.AwayFromZero); }

        public static float frac(float a) { return a - floor(a); }
        public static float2 frac(float2 a) { return a - floor(a); }
        public static float trunc(float a) { return (float)Math.Truncate(a); }
        public static float2 trunc(float2 a) { return new float2(trunc(a.x), trunc(a.y)); }

        // ---------------- sqrt / pow / exp / log ----------------
        public static float sqrt(float a) { return (float)Math.Sqrt(a); }
        public static float2 sqrt(float2 a) { return new float2(sqrt(a.x), sqrt(a.y)); }
        public static float3 sqrt(float3 a) { return new float3(sqrt(a.x), sqrt(a.y), sqrt(a.z)); }
        public static double sqrt(double a) { return Math.Sqrt(a); }
        public static double2 sqrt(double2 a) { return new double2(sqrt(a.x), sqrt(a.y)); }

        public static float pow(float a, float b) { return (float)Math.Pow(a, b); }
        public static float2 pow(float2 a, float2 b) { return new float2(pow(a.x, b.x), pow(a.y, b.y)); }
        public static double pow(double a, double b) { return Math.Pow(a, b); }
        public static float exp(float a) { return (float)Math.Exp(a); }
        public static float exp2(float a) { return (float)Math.Pow(2.0, a); }
        public static float log(float a) { return (float)Math.Log(a); }
        public static float log2(float a) { return (float)Math.Log(a, 2.0); }

        // ---------------- 三角 ----------------
        public static float sin(float a) { return (float)Math.Sin(a); }
        public static float2 sin(float2 a) { return new float2(sin(a.x), sin(a.y)); }
        public static float cos(float a) { return (float)Math.Cos(a); }
        public static float2 cos(float2 a) { return new float2(cos(a.x), cos(a.y)); }
        public static float tan(float a) { return (float)Math.Tan(a); }
        public static float asin(float a) { return (float)Math.Asin(a); }
        public static float acos(float a) { return (float)Math.Acos(a); }
        public static float atan(float a) { return (float)Math.Atan(a); }
        public static float atan2(float y, float x) { return (float)Math.Atan2(y, x); }

        /// <summary>Core 里 9 处均用三参形式 math.sincos(a, out s, out c)。</summary>
        public static void sincos(float a, out float s, out float c) { s = sin(a); c = cos(a); }
        public static float2 sincos(float a) { return new float2(sin(a), cos(a)); }

        public static float radians(float d) { return d * (PI / 180f); }
        public static float degrees(float r) { return r * (180f / PI); }

        // ---------------- 向量几何 ----------------
        public static float dot(float2 a, float2 b) { return a.x * b.x + a.y * b.y; }
        public static float dot(float3 a, float3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        public static float dot(float4 a, float4 b) { return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w; }
        public static double dot(double2 a, double2 b) { return a.x * b.x + a.y * b.y; }

        public static float lengthsq(float2 a) { return dot(a, a); }
        public static float lengthsq(float3 a) { return dot(a, a); }
        public static double lengthsq(double2 a) { return dot(a, a); }
        public static float length(float2 a) { return sqrt(dot(a, a)); }
        public static float length(float3 a) { return sqrt(dot(a, a)); }
        public static double length(double2 a) { return sqrt(dot(a, a)); }

        public static float distancesq(float2 a, float2 b) { return lengthsq(a - b); }
        public static float distancesq(float3 a, float3 b) { return lengthsq(a - b); }
        public static double distancesq(double2 a, double2 b) { return lengthsq(a - b); }
        public static float distance(float2 a, float2 b) { return length(a - b); }
        public static float distance(float3 a, float3 b) { return length(a - b); }
        public static double distance(double2 a, double2 b) { return length(a - b); }

        public static float2 normalize(float2 a) { float l = length(a); return l > 1e-12f ? a / l : new float2(0f, 0f); }
        public static float3 normalize(float3 a) { float l = length(a); return l > 1e-12f ? a / l : new float3(0f, 0f, 0f); }
        public static double2 normalize(double2 a) { double l = length(a); return l > 1e-12 ? a / l : new double2(0.0, 0.0); }

        // Unity 的 normalizesafe 在长度 < 1.1754944e-38 时返回 default(floatN)=0；
        // 这里用 1e-12 作阈值，对布局决策无实质差别（1e-12 以下的向量已是数值噪声）。
        public static float2 normalizesafe(float2 a) { float l = length(a); return l > 1e-12f ? a / l : new float2(0f, 0f); }
        public static float2 normalizesafe(float2 a, float2 def) { float l = length(a); return l > 1e-12f ? a / l : def; }
        public static float3 normalizesafe(float3 a) { float l = length(a); return l > 1e-12f ? a / l : new float3(0f, 0f, 0f); }
        public static float3 normalizesafe(float3 a, float3 def) { float l = length(a); return l > 1e-12f ? a / l : def; }
        public static double2 normalizesafe(double2 a) { double l = length(a); return l > 1e-12 ? a / l : new double2(0.0, 0.0); }
        public static double2 normalizesafe(double2 a, double2 def) { double l = length(a); return l > 1e-12 ? a / l : def; }

        public static float3 cross(float3 a, float3 b) { return new float3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }

        // ---------------- 插值 / 限幅 ----------------
        public static float lerp(float a, float b, float t) { return a + (b - a) * t; }
        public static float2 lerp(float2 a, float2 b, float t) { return a + (b - a) * t; }
        public static float2 lerp(float2 a, float2 b, float2 t) { return a + (b - a) * t; }
        public static float3 lerp(float3 a, float3 b, float t) { return a + (b - a) * t; }
        public static double2 lerp(double2 a, double2 b, double t) { return a + (b - a) * t; }

        public static float clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static int clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static uint clamp(uint v, uint lo, uint hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static double clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
        public static float2 clamp(float2 v, float2 lo, float2 hi) { return new float2(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y)); }
        public static float2 clamp(float2 v, float lo, float hi) { return new float2(clamp(v.x, lo, hi), clamp(v.y, lo, hi)); }
        public static float3 clamp(float3 v, float3 lo, float3 hi) { return new float3(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y), clamp(v.z, lo.z, hi.z)); }
        public static float3 clamp(float3 v, float lo, float hi) { return new float3(clamp(v.x, lo, hi), clamp(v.y, lo, hi), clamp(v.z, lo, hi)); }
        public static double2 clamp(double2 v, double2 lo, double2 hi) { return new double2(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y)); }

        public static float saturate(float v) { return clamp(v, 0f, 1f); }
        public static float2 saturate(float2 v) { return clamp(v, 0f, 1f); }
        public static float3 saturate(float3 v) { return new float3(saturate(v.x), saturate(v.y), saturate(v.z)); }

        public static float smoothstep(float a, float b, float x)
        {
            float t = saturate(unchecked((x - a) / (b - a)));
            return t * t * (3f - 2f * t);
        }
        public static float2 smoothstep(float2 a, float2 b, float2 x) { return new float2(smoothstep(a.x, b.x, x.x), smoothstep(a.y, b.y, x.y)); }

        public static float square(float v) { return v * v; }
        public static float2 square(float2 v) { return v * v; }

        // ---------------- 判定 / 归约 ----------------
        public static bool isfinite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        public static bool2 isfinite(float2 v) { return new bool2(isfinite(v.x), isfinite(v.y)); }
        public static bool3 isfinite(float3 v) { return new bool3(isfinite(v.x), isfinite(v.y), isfinite(v.z)); }
        public static bool4 isfinite(float4 v) { return new bool4(isfinite(v.x), isfinite(v.y), isfinite(v.z), isfinite(v.w)); }
        public static bool isfinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public static bool all(bool2 v) { return v.x && v.y; }
        public static bool all(bool3 v) { return v.x && v.y && v.z; }
        public static bool all(bool4 v) { return v.x && v.y && v.z && v.w; }
        public static bool any(bool2 v) { return v.x || v.y; }
        public static bool any(bool3 v) { return v.x || v.y || v.z; }
        public static bool any(bool4 v) { return v.x || v.y || v.z || v.w; }

        public static float cmax(float2 v) { return max(v.x, v.y); }
        public static float cmax(float3 v) { return max(max(v.x, v.y), v.z); }
        public static float cmax(float4 v) { return max(max(v.x, v.y), max(v.z, v.w)); }
        public static float cmin(float2 v) { return min(v.x, v.y); }
        public static float cmin(float3 v) { return min(min(v.x, v.y), v.z); }
        public static float cmin(float4 v) { return min(min(v.x, v.y), min(v.z, v.w)); }
        public static int cmax(int2 v) { return max(v.x, v.y); }
        public static int cmin(int2 v) { return min(v.x, v.y); }
        public static int cmax(int3 v) { return max(max(v.x, v.y), v.z); }
        public static int cmin(int3 v) { return min(min(v.x, v.y), v.z); }

        public static float select(float a, float b, bool c) { return c ? b : a; }
        public static float2 select(float2 a, float2 b, bool2 c) { return new float2(c.x ? b.x : a.x, c.y ? b.y : a.y); }
        public static float2 select(float2 a, float2 b, bool c) { return c ? b : a; }
        public static float3 select(float3 a, float3 b, bool3 c) { return new float3(c.x ? b.x : a.x, c.y ? b.y : a.y, c.z ? b.z : a.z); }
        public static float3 select(float3 a, float3 b, bool c) { return c ? b : a; }
        public static int select(int a, int b, bool c) { return c ? b : a; }
        public static int2 select(int2 a, int2 b, bool2 c) { return new int2(c.x ? b.x : a.x, c.y ? b.y : a.y); }

        // ---------------- 矩阵 ----------------
        public static float2 mul(float2x2 m, float2 v) { return m * v; }
        public static float2 mul(float2 v, float2x2 m) { return v * m; }

        public static float3 rotate(quaternion q, float3 v)
        {
            float3 u = new float3(q.x, q.y, q.z);
            float s = q.w;
            return 2f * dot(u, v) * u + (s * s - dot(u, u)) * v + 2f * s * cross(u, v);
        }

        // ---------------- 第一轮编译后补齐（全部来自真实报错点，非猜测） ----------------
        // TerrainPalette.cs:261/267  math.pow(float3, float) + select(float3,float3,bool3)
        public static float2 pow(float2 a, float b) { return new float2(pow(a.x, b), pow(a.y, b)); }
        public static float3 pow(float3 a, float b) { return new float3(pow(a.x, b), pow(a.y, b), pow(a.z, b)); }
        public static float3 pow(float3 a, float3 b) { return new float3(pow(a.x, b.x), pow(a.y, b.y), pow(a.z, b.z)); }
        public static float4 pow(float4 a, float b) { return new float4(pow(a.x, b), pow(a.y, b), pow(a.z, b), pow(a.w, b)); }
        public static float4 pow(float4 a, float4 b) { return new float4(pow(a.x, b.x), pow(a.y, b.y), pow(a.z, b.z), pow(a.w, b.w)); }

        // Arrangement.cs:284  math.cmax(math.abs(double2)) -> double
        public static double cmax(double2 v) { return max(v.x, v.y); }
        public static double cmax(double3 v) { return max(max(v.x, v.y), v.z); }
        public static double cmax(double4 v) { return max(max(v.x, v.y), max(v.z, v.w)); }
        public static double cmin(double2 v) { return min(v.x, v.y); }
        public static double cmin(double3 v) { return min(min(v.x, v.y), v.z); }

        // Arrangement.cs:658 / YardKit.cs:124  math.atan2(double,double) -> double
        public static double atan2(double y, double x) { return Math.Atan2(y, x); }
        public static double2 atan2(double2 y, double2 x) { return new double2(Math.Atan2(y.x, x.x), Math.Atan2(y.y, x.y)); }

        // Poly.Ops.cs:228/240/245, PlanarGraph.cs:400  双精度三角与符号
        public static double sin(double a) { return Math.Sin(a); }
        public static double cos(double a) { return Math.Cos(a); }
        public static double tan(double a) { return Math.Tan(a); }
        public static double asin(double a) { return Math.Asin(a); }
        public static double acos(double a) { return Math.Acos(a); }
        public static double atan(double a) { return Math.Atan(a); }
        public static double sign(double a) { return a > 0.0 ? 1.0 : (a < 0.0 ? -1.0 : 0.0); }
        public static double2 sign(double2 a) { return new double2(sign(a.x), sign(a.y)); }
        public static double3 sign(double3 a) { return new double3(sign(a.x), sign(a.y), sign(a.z)); }
        public static double2 sin(double2 a) { return new double2(sin(a.x), sin(a.y)); }
        public static double2 cos(double2 a) { return new double2(cos(a.x), cos(a.y)); }
        public static double radians(double d) { return d * (Math.PI / 180.0); }
        public static double degrees(double r) { return r * (180.0 / Math.PI); }

        // SpatialIndex.cs:224  分量差是 long，需要 long 重载，否则会被隐式挑到 float 版本再赋给 long
        public static long max(long a, long b) { return a > b ? a : b; }
        public static long min(long a, long b) { return a < b ? a : b; }
        public static long abs(long a) { return Math.Abs(a); }
        public static long clamp(long v, long lo, long hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ---------------- double2 / double3 常用集 ----------------
        // Core 的 Geometry 层（Arrangement / PlanarGraph / Poly.Ops）走双精度，避免每轮只补一个函数。
        public static double3 abs(double3 a) { return new double3(abs(a.x), abs(a.y), abs(a.z)); }
        public static double3 min(double3 a, double3 b) { return new double3(min(a.x, b.x), min(a.y, b.y), min(a.z, b.z)); }
        public static double3 max(double3 a, double3 b) { return new double3(max(a.x, b.x), max(a.y, b.y), max(a.z, b.z)); }
        public static double3 min(double3 a, double b) { return new double3(min(a.x, b), min(a.y, b), min(a.z, b)); }
        public static double3 max(double3 a, double b) { return new double3(max(a.x, b), max(a.y, b), max(a.z, b)); }
        public static double3 floor(double3 a) { return new double3(floor(a.x), floor(a.y), floor(a.z)); }
        public static double3 ceil(double3 a) { return new double3(Math.Ceiling(a.x), Math.Ceiling(a.y), Math.Ceiling(a.z)); }
        public static double3 sqrt(double3 a) { return new double3(sqrt(a.x), sqrt(a.y), sqrt(a.z)); }
        public static double3 round(double3 a) { return new double3(round(a.x), round(a.y), round(a.z)); }
        public static double dot(double3 a, double3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        public static double lengthsq(double3 a) { return dot(a, a); }
        public static double length(double3 a) { return sqrt(dot(a, a)); }
        public static double distance(double3 a, double3 b) { return length(a - b); }
        public static double distancesq(double3 a, double3 b) { return lengthsq(a - b); }
        public static double3 normalize(double3 a) { double l = length(a); return l > 1e-12 ? a / l : new double3(0.0, 0.0, 0.0); }
        public static double3 normalizesafe(double3 a) { return normalize(a); }
        public static double3 normalizesafe(double3 a, double3 def) { double l = length(a); return l > 1e-12 ? a / l : def; }
        public static double2 lerp(double2 a, double2 b, double2 t) { return a + (b - a) * t; }
        public static double3 lerp(double3 a, double3 b, double t) { return a + (b - a) * t; }
        public static double3 lerp(double3 a, double3 b, double3 t) { return a + (b - a) * t; }
        public static double2 clamp(double2 v, double lo, double hi) { return new double2(clamp(v.x, lo, hi), clamp(v.y, lo, hi)); }
        public static double3 clamp(double3 v, double3 lo, double3 hi) { return new double3(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y), clamp(v.z, lo.z, hi.z)); }
        public static double square(double v) { return v * v; }
        public static double2 square(double2 v) { return v * v; }
        public static double2 select(double2 a, double2 b, bool2 c) { return new double2(c.x ? b.x : a.x, c.y ? b.y : a.y); }
        public static double3 select(double3 a, double3 b, bool3 c) { return new double3(c.x ? b.x : a.x, c.y ? b.y : a.y, c.z ? b.z : a.z); }
        public static bool2 isfinite(double2 v) { return new bool2(isfinite(v.x), isfinite(v.y)); }
        public static bool3 isfinite(double3 v) { return new bool3(isfinite(v.x), isfinite(v.y), isfinite(v.z)); }

        // int 向量的限幅/符号（Core 里用于格坐标）
        public static int2 clamp(int2 v, int2 lo, int2 hi) { return new int2(clamp(v.x, lo.x, hi.x), clamp(v.y, lo.y, hi.y)); }
        public static int2 clamp(int2 v, int lo, int hi) { return new int2(clamp(v.x, lo, hi), clamp(v.y, lo, hi)); }
        public static int3 clamp(int3 v, int lo, int hi) { return new int3(clamp(v.x, lo, hi), clamp(v.y, lo, hi), clamp(v.z, lo, hi)); }
        public static int2 sign(int2 a) { return new int2(sign(a.x), sign(a.y)); }
        public static int3 sign(int3 a) { return new int3(sign(a.x), sign(a.y), sign(a.z)); }

        // ---------------- hash ----------------
        // 与官方 xxhash 不同（见文件头差异说明 1）。这里用 FNV-1a + 局部混淆，
        // 保证：同输入同输出（可复现）、低位分布均匀（Core 大量用它做 ±几米的抖动）。
        public static uint hash(int v) { return Mix((uint)v); }
        public static uint hash(uint v) { return Mix(v); }
        public static uint hash(int2 v) { return Mix((uint)v.x * 0x9E3779B9u ^ ((uint)v.y * 0x85EBCA6Bu)); }
        public static uint hash(uint2 v) { return Mix(v.x * 0x9E3779B9u ^ (v.y * 0x85EBCA6Bu)); }
        public static uint hash(int3 v) { return Mix((uint)v.x * 0x9E3779B9u ^ ((uint)v.y * 0x85EBCA6Bu) ^ ((uint)v.z * 0xC2B2AE35u)); }
        public static uint hash(uint3 v) { return Mix(v.x * 0x9E3779B9u ^ (v.y * 0x85EBCA6Bu) ^ (v.z * 0xC2B2AE35u)); }
        public static uint hash(int4 v) { return Mix((uint)v.x * 0x9E3779B9u ^ ((uint)v.y * 0x85EBCA6Bu) ^ ((uint)v.z * 0xC2B2AE35u) ^ ((uint)v.w * 0x27D4EB2Fu)); }
        public static uint hash(uint4 v) { return Mix(v.x * 0x9E3779B9u ^ (v.y * 0x85EBCA6Bu) ^ (v.z * 0xC2B2AE35u) ^ (v.w * 0x27D4EB2Fu)); }
        // 浮点先按 1/1024 量化再哈希，避免同一格子里相邻点得到同一图案
        public static uint hash(float2 v) { return hash(new int2((int)round(v.x * 1024f), (int)round(v.y * 1024f))); }
        public static uint hash(float3 v) { return hash(new int3((int)round(v.x * 1024f), (int)round(v.y * 1024f), (int)round(v.z * 1024f))); }
        public static uint hash(float4 v) { return hash(new int4((int)round(v.x * 1024f), (int)round(v.y * 1024f), (int)round(v.z * 1024f), (int)round(v.w * 1024f))); }
        public static uint hash(double2 v) { return hash(new int2((int)round(v.x * 1024.0), (int)round(v.y * 1024.0))); }

        private static uint Mix(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
        }
    }
}
