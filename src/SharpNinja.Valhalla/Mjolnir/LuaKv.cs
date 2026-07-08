// Lua-semantics key/value table used by the graph.lua tag transforms.
// Source semantics: lua/graph.lua @ 3.7.0
//
// graph.lua manipulates a `kv` table of OSM tags. The transform stores back into the
// same table strings like "true"/"false", numbers, or nil (by clearing the key). To be
// faithful we must reproduce Lua's value model and truthiness exactly:
//   * Missing key -> nil.
//   * nil and the boolean false are the only falsey values.
//   * The STRING "false" is TRUTHY in Lua (this matters for `a or b`).
//   * `a or b` returns a if a is truthy else b.
//
// We therefore model a value as one of: Nil, a Bool, a Number (double), or a String.
// The transforms read/write through this table so each Lua expression maps 1:1.

using System.Collections.Generic;
using System.Globalization;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>A single Lua value: nil, boolean, number, or string. Faithfully reproduces Lua truthiness.</summary>
internal readonly struct LuaValue
{
    private enum Kind : byte
    {
        Nil = 0,
        Bool = 1,
        Number = 2,
        String = 3,
    }

    private readonly Kind _kind;
    private readonly bool _bool;
    private readonly double _number;
    private readonly string? _string;

    private LuaValue(Kind kind, bool b, double n, string? s)
    {
        _kind = kind;
        _bool = b;
        _number = n;
        _string = s;
    }

    /// <summary>The Lua <c>nil</c> value.</summary>
    public static readonly LuaValue Nil = new(Kind.Nil, false, 0, null);

    public static LuaValue Bool(bool b) => new(Kind.Bool, b, 0, null);

    public static LuaValue Number(double n) => new(Kind.Number, false, n, null);

    public static LuaValue Str(string? s) => s == null ? Nil : new(Kind.String, false, 0, s);

    /// <summary>True if this value is nil.</summary>
    public bool IsNil => _kind == Kind.Nil;

    /// <summary>Lua truthiness: everything is truthy except nil and boolean false.</summary>
    public bool IsTruthy => _kind switch
    {
        Kind.Nil => false,
        Kind.Bool => _bool,
        _ => true,
    };

    /// <summary>Lua equality against a string literal (number is stringified Lua-style).</summary>
    public bool EqualsString(string other) => AsLuaString() == other;

    /// <summary>The string form, or null if nil (used when storing back into the OSM-tag dictionary).</summary>
    public string? AsLuaString()
    {
        switch (_kind)
        {
            case Kind.Nil:
                return null;
            case Kind.Bool:
                return _bool ? "true" : "false";
            case Kind.Number:
                // Lua tostring of an integer-valued number prints without a decimal point.
                if (_number == System.Math.Floor(_number) && !double.IsInfinity(_number))
                {
                    return ((long)_number).ToString(CultureInfo.InvariantCulture);
                }

                return _number.ToString(CultureInfo.InvariantCulture);
            default:
                return _string;
        }
    }
}

/// <summary>
/// Lua-style table of OSM tags. Reading a missing key yields <see cref="LuaValue.Nil"/>;
/// writing nil removes the key. Backed by a plain string dictionary so callers can read
/// the final normalized tag set. String/number/bool values round-trip via Lua stringify.
/// </summary>
internal sealed class LuaKv
{
    private readonly Dictionary<string, string> _map;

    public LuaKv(IEnumerable<KeyValuePair<string, string>> initial)
    {
        _map = new Dictionary<string, string>();
        foreach (KeyValuePair<string, string> kvp in initial)
        {
            _map[kvp.Key] = kvp.Value;
        }
    }

    public LuaKv(IReadOnlyDictionary<string, string> initial)
    {
        _map = new Dictionary<string, string>(initial.Count);
        foreach (KeyValuePair<string, string> kvp in initial)
        {
            _map[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>Number of tags currently in the table (Lua-side this is non-trivial; we only need count of keys).</summary>
    public int Count => _map.Count;

    /// <summary>The raw, normalized tag dictionary after transformation.</summary>
    public IReadOnlyDictionary<string, string> Raw => _map;

    /// <summary>Reads a tag as a <see cref="LuaValue"/> (nil if absent).</summary>
    public LuaValue Get(string key) =>
        _map.TryGetValue(key, out string? v) ? LuaValue.Str(v) : LuaValue.Nil;

    /// <summary>Reads a tag's raw string, or null if absent.</summary>
    public string? GetString(string key) => _map.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Writes a tag value; a nil value removes the key.</summary>
    public void Set(string key, LuaValue value)
    {
        string? s = value.AsLuaString();
        if (s == null)
        {
            _map.Remove(key);
        }
        else
        {
            _map[key] = s;
        }
    }

    /// <summary>Writes a raw string (or removes the key when null).</summary>
    public void SetString(string key, string? value)
    {
        if (value == null)
        {
            _map.Remove(key);
        }
        else
        {
            _map[key] = value;
        }
    }

    /// <summary>Writes a boolean value as the Lua "true"/"false" strings used by graph.lua.</summary>
    public void SetBool(string key, bool value) => _map[key] = value ? "true" : "false";

    /// <summary>True if the tag equals the given string literal.</summary>
    public bool Eq(string key, string literal) => GetString(key) == literal;

    /// <summary>True if the tag is present (Lua <c>~= nil</c>).</summary>
    public bool Has(string key) => _map.ContainsKey(key);

    /// <summary>Removes a key.</summary>
    public void Remove(string key) => _map.Remove(key);
}
