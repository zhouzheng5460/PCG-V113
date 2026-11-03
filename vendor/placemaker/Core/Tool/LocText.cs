using System;

namespace Placemaker.Core.Tool
{
    /// <summary>Localisation key + positional args, mirrors contract.ts <c>LocText</c>.</summary>
    public sealed class LocText : IEquatable<LocText>
    {
        public readonly string Key;
        public readonly object[] Args;

        public LocText(string key, params object[] args)
        {
            Key = key ?? "";
            Args = args ?? Array.Empty<object>();
        }

        public static LocText Of(string key, params object[] args) => string.IsNullOrEmpty(key) ? null : new LocText(key, args);

        public bool Equals(LocText other)
        {
            if (other == null || other.Key != Key || other.Args.Length != Args.Length) return false;
            for (int i = 0; i < Args.Length; i++) if (!Equals(Args[i], other.Args[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => Equals(obj as LocText);
        public override int GetHashCode() => Key.GetHashCode();
        public override string ToString() => Args.Length == 0 ? Key : Key + "(" + string.Join(",", Args) + ")";

        public JsonValue ToJson()
        {
            var o = JsonValue.Obj().Set("key", Key);
            if (Args.Length > 0)
            {
                var a = JsonValue.Arr();
                foreach (var x in Args)
                {
                    if (x is IConvertible c && !(x is string) && !(x is bool))
                        a.Add(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
                    else a.Add(Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture));
                }
                o.Set("args", a);
            }
            return o;
        }
    }
}
