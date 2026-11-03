// Placemaker — minimal JSON DOM, parser and writer.
//
// Why our own: Core must stay engine-free and unit-testable on net9 without the game's
// Newtonsoft build, and the UI contract / recipe files need exact, culture-invariant output
// (a German Windows locale must never turn 1.5 into "1,5"). The parser is tolerant on purpose:
// recipe files are hand-written by layer authors and players, so comments and trailing commas
// are accepted and unknown fields are simply ignored by the readers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Placemaker.Core.Tool
{
    public enum JsonKind { Null = 0, Bool = 1, Number = 2, String = 3, Array = 4, Object = 5 }

    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        public static readonly JsonValue True = new JsonValue(JsonKind.Bool) { _bool = true };
        public static readonly JsonValue False = new JsonValue(JsonKind.Bool) { _bool = false };

        public JsonKind Kind { get; }
        private bool _bool;
        private double _number;
        private string _string;
        private List<JsonValue> _items;
        private List<KeyValuePair<string, JsonValue>> _props;
        private Dictionary<string, int> _index;

        private JsonValue(JsonKind kind) { Kind = kind; }

        public static JsonValue Bool(bool b) => b ? True : False;
        public static JsonValue Num(double d) => new JsonValue(JsonKind.Number) { _number = d };
        public static JsonValue Str(string s) => s == null ? Null : new JsonValue(JsonKind.String) { _string = s };
        public static JsonValue Arr() => new JsonValue(JsonKind.Array) { _items = new List<JsonValue>() };
        public static JsonValue Obj() => new JsonValue(JsonKind.Object)
        {
            _props = new List<KeyValuePair<string, JsonValue>>(),
            _index = new Dictionary<string, int>(StringComparer.Ordinal),
        };

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;

        public bool AsBool(bool fallback = false) => Kind == JsonKind.Bool ? _bool : fallback;
        public double AsNumber(double fallback = 0) => Kind == JsonKind.Number ? _number : fallback;
        public string AsString(string fallback = "") => Kind == JsonKind.String ? _string : fallback;

        public int Count => Kind == JsonKind.Array ? _items.Count : Kind == JsonKind.Object ? _props.Count : 0;
        public IReadOnlyList<JsonValue> Items => _items ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Properties =>
            _props ?? (IReadOnlyList<KeyValuePair<string, JsonValue>>)Array.Empty<KeyValuePair<string, JsonValue>>();

        /// <summary>Property lookup; returns <see cref="Null"/> for missing keys or non-objects so readers can chain safely.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (Kind != JsonKind.Object || key == null) return Null;
                return _index.TryGetValue(key, out int i) ? _props[i].Value : Null;
            }
        }

        public bool Has(string key) => Kind == JsonKind.Object && key != null && _index.ContainsKey(key);

        // ── builders (fluent, used by the UI model writers and recipe serializer) ──

        public JsonValue Set(string key, JsonValue value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Not an object");
            value = value ?? Null;
            if (_index.TryGetValue(key, out int i)) _props[i] = new KeyValuePair<string, JsonValue>(key, value);
            else { _index[key] = _props.Count; _props.Add(new KeyValuePair<string, JsonValue>(key, value)); }
            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, Str(value));
        public JsonValue Set(string key, double value) => Set(key, Num(value));
        public JsonValue Set(string key, bool value) => Set(key, Bool(value));

        public JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Not an array");
            _items.Add(value ?? Null);
            return this;
        }

        public JsonValue Add(string value) => Add(Str(value));
        public JsonValue Add(double value) => Add(Num(value));

        // ── output ──

        public override string ToString() => Write(false);

        public string Write(bool pretty)
        {
            var sb = new StringBuilder(256);
            WriteTo(sb, pretty, 0);
            return sb.ToString();
        }

        private void WriteTo(StringBuilder sb, bool pretty, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(_bool ? "true" : "false"); break;
                case JsonKind.Number: WriteNumber(sb, _number); break;
                case JsonKind.String: WriteString(sb, _string); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (pretty) NewLine(sb, depth + 1);
                        _items[i].WriteTo(sb, pretty, depth + 1);
                    }
                    if (pretty && _items.Count > 0) NewLine(sb, depth);
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _props.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (pretty) NewLine(sb, depth + 1);
                        WriteString(sb, _props[i].Key);
                        sb.Append(pretty ? ": " : ":");
                        _props[i].Value.WriteTo(sb, pretty, depth + 1);
                    }
                    if (pretty && _props.Count > 0) NewLine(sb, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        public static void WriteNumber(StringBuilder sb, double d)
        {
            // JSON has no NaN/Infinity; null keeps the document parseable (UI treats it as "unknown").
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── input ──

        /// <summary>Parses JSON (comments and trailing commas tolerated). Throws <see cref="FormatException"/> with a position on error.</summary>
        public static JsonValue Parse(string text)
        {
            var p = new Parser(text ?? "");
            p.SkipWs();
            var v = p.ReadValue(0);
            p.SkipWs();
            if (!p.AtEnd) throw p.Error("Unexpected trailing content");
            return v;
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (FormatException e) { value = Null; error = e.Message; return false; }
        }

        private sealed class Parser
        {
            private const int MaxDepth = 64;
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s; }
            public bool AtEnd => _i >= _s.Length;

            public FormatException Error(string msg)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++) { if (_s[k] == '\n') { line++; col = 1; } else col++; }
                return new FormatException(msg + " at line " + line + ", column " + col);
            }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') { _i++; continue; }
                    if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        continue;
                    }
                    if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                    {
                        int end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        _i = end < 0 ? _s.Length : end + 2;
                        continue;
                    }
                    break;
                }
            }

            public JsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Error("Nesting too deep");
                if (AtEnd) throw Error("Unexpected end of input");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return Str(ReadString());
                    case 't': Expect("true"); return True;
                    case 'f': Expect("false"); return False;
                    case 'n': Expect("null"); return Null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num(ReadNumber());
                        throw Error("Unexpected character '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) throw Error("Expected '" + word + "'");
                _i += word.Length;
            }

            private JsonValue ReadObject(int depth)
            {
                var o = Obj();
                _i++;
                SkipWs();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated object");
                    if (_s[_i] == '}') { _i++; return o; }
                    if (_s[_i] != '"') throw Error("Expected property name");
                    string key = ReadString();
                    SkipWs();
                    if (AtEnd || _s[_i] != ':') throw Error("Expected ':'");
                    _i++;
                    SkipWs();
                    o.Set(key, ReadValue(depth + 1));
                    SkipWs();
                    if (AtEnd) throw Error("Unterminated object");
                    if (_s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Error("Expected ',' or '}'");
                }
            }

            private JsonValue ReadArray(int depth)
            {
                var a = Arr();
                _i++;
                SkipWs();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated array");
                    if (_s[_i] == ']') { _i++; return a; }
                    a.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (AtEnd) throw Error("Unterminated array");
                    if (_s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _i++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw Error("Unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("Bad unicode escape");
                            if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                throw Error("Bad unicode escape");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default: throw Error("Bad escape '\\" + e + "'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                string token = _s.Substring(start, _i - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw Error("Bad number '" + token + "'");
                return d;
            }
        }
    }
}
