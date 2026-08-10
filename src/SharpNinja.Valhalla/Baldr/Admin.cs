// Faithful C# port of Valhalla baldr Admin (admin.h + src/baldr/admin.cc) @ 3.7.0.
// Source: valhalla/baldr/admin.h, valhalla/src/baldr/admin.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// EXACT BYTE LAYOUT (read directly from the on-disk tile blob):
//   offset 0  : uint32 country_offset_   (4 bytes)  - country name offset
//   offset 4  : uint32 state_offset_     (4 bytes)  - state name offset
//   offset 8  : char[2] country_iso_     (2 bytes)  - ISO3166-1
//   offset 10 : char[3] state_iso_       (3 bytes)  - ISO3166-2
//   offset 13 : char[3] spare_           (3 bytes)  - byte alignment padding
// Total struct size: 16 bytes.
//
// The char arrays are reproduced as individual byte fields (the project does not
// enable AllowUnsafeBlocks, so inline fixed-size buffers are not available). The
// field order and Pack=1 reproduce the C++ std::array<char,N> + char[3] layout
// exactly, so a tile byte buffer parses identically.

using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Holds a generic admin with state and country iso and text. Text is stored
/// in the GraphTile text list and the offset is stored within the admin.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: this struct is byte-packed and read directly from the
/// on-disk tile blob. Size is exactly 16 bytes. See file header for the byte map.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Admin
{
    /// <summary>Length of a state ISO3166-2 code buffer (chars).</summary>
    public const int StateIso = 3;

    /// <summary>Length of a country ISO3166-1 code buffer (chars).</summary>
    public const int CountryIso = 2;

    private uint _countryOffset; // country name offset
    private uint _stateOffset;   // state name offset

    // country_iso_[2]  (ISO3166-1)
    private byte _countryIso0;
    private byte _countryIso1;

    // state_iso_[3]    (ISO3166-2)
    private byte _stateIso0;
    private byte _stateIso1;
    private byte _stateIso2;

    // spare_[3]        (byte alignment)
    private byte _spare0;
    private byte _spare1;
    private byte _spare2;

    /// <summary>
    /// Constructor given parameters.
    /// </summary>
    /// <param name="countryOffset">Offset to country name in text records.</param>
    /// <param name="stateOffset">Offset to state name in text records.</param>
    /// <param name="countryIso">Country ISO string.</param>
    /// <param name="stateIso">State ISO string.</param>
    public Admin(uint countryOffset, uint stateOffset, string countryIso, string stateIso)
    {
        _countryOffset = countryOffset;
        _stateOffset = stateOffset;

        // std::array<> members are value-initialized to '\0' in the C++ struct.
        _countryIso0 = 0;
        _countryIso1 = 0;
        _stateIso0 = 0;
        _stateIso1 = 0;
        _stateIso2 = 0;
        _spare0 = 0;
        _spare1 = 0;
        _spare2 = 0;

        // Example:  GB or US
        if (countryIso.Length == CountryIso)
        {
            _countryIso0 = (byte)countryIso[0];
            _countryIso1 = (byte)countryIso[1];
        }
        else
        {
            _countryIso0 = (byte)'\0';
        }

        switch (stateIso.Length)
        {
            case StateIso - 1:
                // Example:  PA
                _stateIso2 = (byte)'\0';
                // [[fallthrough]]
                CopyStateIso(stateIso);
                break;
            case StateIso:
                // Example:  WLS
                CopyStateIso(stateIso);
                break;
            default:
                _stateIso0 = (byte)'\0';
                break;
        }
    }

    private void CopyStateIso(string stateIso)
    {
        // std::copy(state_iso.begin(), state_iso.end(), state_iso_.begin())
        for (int i = 0; i < stateIso.Length && i < StateIso; i++)
        {
            switch (i)
            {
                case 0:
                    _stateIso0 = (byte)stateIso[i];
                    break;
                case 1:
                    _stateIso1 = (byte)stateIso[i];
                    break;
                case 2:
                    _stateIso2 = (byte)stateIso[i];
                    break;
            }
        }
    }

    /// <summary>
    /// Gets the offset into the GraphTile text list for the state text associated with the admin.
    /// </summary>
    public readonly uint StateOffset => _stateOffset;

    /// <summary>
    /// Gets the offset into the GraphTile text list for the country text associated with the admin.
    /// </summary>
    public readonly uint CountryOffset => _countryOffset;

    /// <summary>
    /// Gets the packed two-byte country ISO code without allocating a managed string.
    /// Zero denotes an unset country code.
    /// </summary>
    internal readonly ushort CountryIsoCodeValue =>
        _countryIso0 == (byte)'\0'
            ? (ushort)0
            : (ushort)(_countryIso0 | (_countryIso1 << 8));

    /// <summary>
    /// Gets the country ISO3166-1 code. Returns an empty string when unset.
    /// </summary>
    public readonly string CountryIsoCode()
    {
        if (_countryIso0 == (byte)'\0')
        {
            return string.Empty;
        }

        // std::string(country_iso_.begin(), country_iso_.end()) - full fixed width.
        return new string(new[] { (char)_countryIso0, (char)_countryIso1 });
    }

    /// <summary>
    /// Gets the state ISO code. Country ISO + dash + state ISO yields ISO3166-2 for the state.
    /// Returns an empty string when unset; for a 2-char code the trailing NUL is stripped (the
    /// C++ copies up to the first '\0').
    /// </summary>
    public readonly string StateIsoCode()
    {
        if (_stateIso0 == (byte)'\0')
        {
            return string.Empty;
        }

        // std::string(state_iso_.begin(), std::find(begin, end, '\0')) - up to first NUL.
        var chars = new char[StateIso];
        int len = 0;
        ReadOnlySpan<byte> bytes = stackalloc byte[StateIso] { _stateIso0, _stateIso1, _stateIso2 };
        for (int i = 0; i < StateIso; i++)
        {
            if (bytes[i] == (byte)'\0')
            {
                break;
            }

            chars[i] = (char)bytes[i];
            len++;
        }

        return new string(chars, 0, len);
    }
}
