// Faithful C# port of Valhalla baldr AdminInfo (admininfo.h) @ 3.7.0.
// Source: valhalla/baldr/admininfo.h
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// AdminInfo is an interface/transfer class (NOT a bit-packed on-disk tile struct);
// it carries resolved country/state text and ISO strings. No fixed byte size applies.

using System;
using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Interface class used to pass information about an administrative area.
/// Encapsulates the country and state text.
/// </summary>
public sealed class AdminInfo : IEquatable<AdminInfo>
{
    private readonly string _countryText;
    private readonly string _stateText;
    private readonly string _countryIso;
    private readonly string _stateIso;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="countryText">Country text string.</param>
    /// <param name="stateText">State text string.</param>
    /// <param name="countryIso">Country iso string.</param>
    /// <param name="stateIso">State iso string.</param>
    public AdminInfo(string countryText, string stateText, string countryIso, string stateIso)
    {
        _countryText = countryText;
        _stateText = stateText;
        _countryIso = countryIso;
        _stateIso = stateIso;
    }

    /// <summary>Returns the country text.</summary>
    public string CountryText => _countryText;

    /// <summary>Returns the state text.</summary>
    public string StateText => _stateText;

    /// <summary>Gets the country iso.</summary>
    public string CountryIso => _countryIso;

    /// <summary>Gets the state iso.</summary>
    public string StateIso => _stateIso;

    /// <summary>
    /// Returns true if the specified object is equal to this object. Mirrors the C++
    /// <c>operator==</c>, comparing country iso/text and state iso/text.
    /// </summary>
    public bool Equals(AdminInfo? rhs)
    {
        if (rhs is null)
        {
            return false;
        }

        return _countryIso == rhs._countryIso && _countryText == rhs._countryText &&
               _stateIso == rhs._stateIso && _stateText == rhs._stateText;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AdminInfo other && Equals(other);

    /// <summary>Equality operator mirroring the C++ <c>operator==</c>.</summary>
    public static bool operator ==(AdminInfo? a, AdminInfo? b)
    {
        if (a is null)
        {
            return b is null;
        }

        return a.Equals(b);
    }

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AdminInfo? a, AdminInfo? b) => !(a == b);

    /// <summary>
    /// Hash mirroring the C++ <c>AdminInfoHasher</c>: hashes the concatenation
    /// country_iso + country_text + state_iso + state_text.
    /// </summary>
    public override int GetHashCode()
        => (_countryIso + _countryText + _stateIso + _stateText).GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Hasher equivalent to the C++ <c>AdminInfo::AdminInfoHasher</c> functor.
    /// </summary>
    public sealed class AdminInfoHasher : IEqualityComparer<AdminInfo>
    {
        /// <inheritdoc/>
        public bool Equals(AdminInfo? x, AdminInfo? y)
        {
            if (x is null)
            {
                return y is null;
            }

            return x.Equals(y);
        }

        /// <inheritdoc/>
        public int GetHashCode(AdminInfo ai)
            => (ai._countryIso + ai._countryText + ai._stateIso + ai._stateText)
                .GetHashCode(StringComparison.Ordinal);
    }
}
