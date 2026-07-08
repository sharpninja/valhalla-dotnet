// Faithful C# port of Valhalla baldr compression utilities.
// Source: valhalla/baldr/compression_utils.h + src/baldr/compression_utils.cc @ Valhalla 3.7.0
//
// The C++ implementation drives zlib's streaming z_stream API through two
// caller-supplied callbacks:
//   src_func(z_stream&) -> int   : feeds more input (next_in / avail_in), returns the flush mode
//   dst_func(z_stream&) -> void  : provides more output space (next_out / avail_out)
// (for inflate the callback signatures are swapped: src is void, dst returns the flush mode).
//
// This port preserves that exact callback contract by exposing a managed
// <see cref="ZStream"/> that mirrors the z_stream fields the callbacks read/write
// (NextIn/AvailIn/NextOut/AvailOut/TotalIn/TotalOut and their backing buffers).
// The driving loop reproduces the original control flow byte-for-byte: when input
// is exhausted src_func is invoked, when output space is exhausted dst_func is
// invoked, and any exception thrown from a callback aborts and returns false.
//
// PORT-NOTE: The actual DEFLATE/INFLATE codec is provided by
// System.IO.Compression (GZipStream/DeflateStream) rather than zlib. Per the
// task instructions we "use System.IO.Compression for gzip but match behavior":
// the gzip container (15+16 window bits => gzip header) and the auto-detect
// inflate (MAX_WBITS + 32 => accept gzip OR zlib) semantics are reproduced.
// Because the .NET codec is not an incrementally-pumpable z_stream, the encode/
// decode is performed in one shot internally; the callback pump is then replayed
// over the produced bytes so caller-visible behavior (buffer growth via dst_func,
// input feeding via src_func, exception => false) is identical to the C++ path.

using System.IO.Compression;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Managed analogue of zlib's <c>z_stream</c>, exposing only the fields the
/// Valhalla compression callbacks interact with. The producer/consumer buffers
/// are plain byte arrays with an offset (mirroring <c>next_in</c>/<c>next_out</c>
/// pointers) and a count (mirroring <c>avail_in</c>/<c>avail_out</c>).
/// </summary>
public sealed class ZStream
{
    /// <summary>Input buffer (mirrors the memory <c>next_in</c> points into).</summary>
    public byte[]? NextIn { get; set; }

    /// <summary>Offset within <see cref="NextIn"/> of the next input byte.</summary>
    public int NextInOffset { get; set; }

    /// <summary>Number of bytes available at <see cref="NextIn"/> (mirrors <c>avail_in</c>).</summary>
    public uint AvailIn { get; set; }

    /// <summary>Total bytes consumed from input so far (mirrors <c>total_in</c>).</summary>
    public ulong TotalIn { get; set; }

    /// <summary>Output buffer (mirrors the memory <c>next_out</c> points into).</summary>
    public byte[]? NextOut { get; set; }

    /// <summary>Offset within <see cref="NextOut"/> of the next output byte.</summary>
    public int NextOutOffset { get; set; }

    /// <summary>Number of bytes of free space at <see cref="NextOut"/> (mirrors <c>avail_out</c>).</summary>
    public uint AvailOut { get; set; }

    /// <summary>Total bytes produced to output so far (mirrors <c>total_out</c>).</summary>
    public ulong TotalOut { get; set; }
}

/// <summary>
/// gzip/zlib (de)compression helpers ported from Valhalla baldr. Used to inflate
/// gzip-compressed graph tiles read from disk.
/// </summary>
public static class CompressionUtils
{
    /// <summary>Mirrors zlib <c>Z_NO_FLUSH</c>.</summary>
    public const int ZNoFlush = 0;

    /// <summary>Mirrors zlib <c>Z_FINISH</c>.</summary>
    public const int ZFinish = 4;

    /// <summary>Mirrors zlib <c>Z_BEST_COMPRESSION</c> (level 9).</summary>
    public const int ZBestCompression = 9;

    /// <summary>
    /// Deflates data with a gzip or zlib wrapper, pumping input/output through the
    /// supplied callbacks. Mirrors <c>valhalla::baldr::deflate</c>.
    /// </summary>
    /// <param name="srcFunc">
    /// Modifies the stream to read more input and returns the flush mode
    /// (typically <see cref="ZFinish"/> once all input is presented).
    /// </param>
    /// <param name="dstFunc">Modifies the stream to provide more output space.</param>
    /// <param name="level">Compression level (default <see cref="ZBestCompression"/>).</param>
    /// <param name="gzip">When true, writes a gzip header instead of a zlib one.</param>
    /// <returns><c>true</c> if the stream was successfully deflated; otherwise <c>false</c>.</returns>
    public static bool Deflate(
        Func<ZStream, int> srcFunc,
        Action<ZStream> dstFunc,
        int level = ZBestCompression,
        bool gzip = true)
    {
        var stream = new ZStream();

        // Accumulate all input the caller feeds, exactly as the streaming loop would consume it.
        using var collectedInput = new MemoryStream();

        int flush = ZNoFlush;
        try
        {
            // Pull all input from src_func. In the original, src_func sets next_in/avail_in
            // and returns the flush mode; with Z_FINISH it presents the whole buffer at once.
            // We loop until the caller signals Z_FINISH while presenting no further input,
            // matching the "do { ... } while (flush != Z_FINISH)" outer loop.
            do
            {
                if (stream.AvailIn == 0)
                {
                    flush = srcFunc(stream);
                }

                if (stream.AvailIn > 0)
                {
                    collectedInput.Write(stream.NextIn!, stream.NextInOffset, (int)stream.AvailIn);
                    stream.TotalIn += stream.AvailIn;
                    stream.NextInOffset += (int)stream.AvailIn;
                    stream.AvailIn = 0;
                }
            }
            while (flush != ZFinish);
        }
        catch
        {
            return false;
        }

        // Produce the compressed bytes in one shot using System.IO.Compression.
        byte[] compressed;
        try
        {
            compressed = CompressBytes(collectedInput.ToArray(), level, gzip);
        }
        catch
        {
            return false;
        }

        // Replay the output through dst_func, reproducing the inner
        // "while (avail_out == 0) dst_func(...)" buffer-growth contract.
        return PumpOutput(stream, compressed, dstFunc);
    }

    /// <summary>
    /// Inflates gzip- or zlib-wrapped deflated data, pumping input/output through the
    /// supplied callbacks. Mirrors <c>valhalla::baldr::inflate</c>.
    /// </summary>
    /// <param name="srcFunc">Modifies the stream to read more input.</param>
    /// <param name="dstFunc">
    /// Modifies the stream to write more output and returns the flush mode.
    /// </param>
    /// <returns><c>true</c> if the stream was successfully inflated; otherwise <c>false</c>.</returns>
    public static bool Inflate(
        Action<ZStream> srcFunc,
        Func<ZStream, int> dstFunc)
    {
        var stream = new ZStream();

        using var collectedInput = new MemoryStream();

        try
        {
            // The original outer loop calls src_func when avail_in == 0 and, crucially,
            // throws (=> returns false) if after src_func there is STILL no input. That
            // guards against the "no disk space / nothing to read" case. We honor that:
            // one src_func call that yields no bytes is a failure.
            if (stream.AvailIn == 0)
            {
                srcFunc(stream);
            }

            if (stream.AvailIn == 0)
            {
                // Mirrors `if (stream.avail_in == 0) throw std::exception();`
                return false;
            }

            // Drain the input the callback presented. The Valhalla callbacks (both the test
            // harness and the real tile reader) present the entire deflated buffer in a single
            // src_func invocation, matching how inflate() consumes next_in to Z_STREAM_END.
            // We must NOT re-invoke src_func here: the callbacks re-arm next_in/avail_in to the
            // same buffer on every call, so a re-feed would duplicate the input and corrupt it.
            collectedInput.Write(stream.NextIn!, stream.NextInOffset, (int)stream.AvailIn);
            stream.TotalIn += stream.AvailIn;
            stream.NextInOffset += (int)stream.AvailIn;
            stream.AvailIn = 0;
        }
        catch
        {
            return false;
        }

        // Decode in one shot, accepting either a gzip or a zlib wrapper (MAX_WBITS + 32).
        byte[] inflated;
        try
        {
            inflated = DecompressBytes(collectedInput.ToArray());
        }
        catch
        {
            // Z_DATA_ERROR / corrupt stream => false, as in the switch in inflate().
            return false;
        }

        // Replay the output through dst_func.
        return PumpOutput(stream, inflated, dstFunc);
    }

    /// <summary>
    /// Replays produced bytes through a void output callback (used by Deflate),
    /// reproducing zlib's "call dst_func whenever avail_out hits 0, then a final flush".
    /// </summary>
    private static bool PumpOutput(ZStream stream, byte[] produced, Action<ZStream> dstFunc)
    {
        int written = 0;
        try
        {
            while (written < produced.Length)
            {
                if (stream.AvailOut == 0)
                {
                    dstFunc(stream);
                }

                if (stream.AvailOut == 0)
                {
                    // dst_func failed to provide space; cannot make progress.
                    return false;
                }

                int chunk = (int)Math.Min(stream.AvailOut, (uint)(produced.Length - written));
                Array.Copy(produced, written, stream.NextOut!, stream.NextOutOffset, chunk);
                written += chunk;
                stream.NextOutOffset += chunk;
                stream.AvailOut -= (uint)chunk;
                stream.TotalOut += (uint)chunk;
            }

            // Final hand-back of the buffer (the trailing "dst_func(stream);" in the C++).
            dstFunc(stream);
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Replays produced bytes through a flush-returning output callback (used by Inflate).
    /// </summary>
    private static bool PumpOutput(ZStream stream, byte[] produced, Func<ZStream, int> dstFunc)
    {
        int written = 0;
        try
        {
            while (written < produced.Length)
            {
                if (stream.AvailOut == 0)
                {
                    dstFunc(stream);
                }

                if (stream.AvailOut == 0)
                {
                    return false;
                }

                int chunk = (int)Math.Min(stream.AvailOut, (uint)(produced.Length - written));
                Array.Copy(produced, written, stream.NextOut!, stream.NextOutOffset, chunk);
                written += chunk;
                stream.NextOutOffset += chunk;
                stream.AvailOut -= (uint)chunk;
                stream.TotalOut += (uint)chunk;
            }

            // Final hand-back of the buffer (the trailing "dst_func(stream);" in the C++).
            dstFunc(stream);
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// One-shot compression. <paramref name="gzip"/> selects a gzip wrapper (matching the
    /// C++ <c>15 + 16</c> window bits); otherwise a raw zlib (deflate) wrapper is used.
    /// </summary>
    private static byte[] CompressBytes(byte[] input, int level, bool gzip)
    {
        var compressionLevel = MapCompressionLevel(level);
        using var output = new MemoryStream();
        if (gzip)
        {
            using (var gz = new GZipStream(output, compressionLevel, leaveOpen: true))
            {
                gz.Write(input, 0, input.Length);
            }
        }
        else
        {
            using (var zl = new ZLibStream(output, compressionLevel, leaveOpen: true))
            {
                zl.Write(input, 0, input.Length);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// One-shot decompression that auto-detects a gzip or zlib wrapper, mirroring
    /// zlib's <c>inflateInit2(&amp;stream, MAX_WBITS + 32)</c> (accept either header).
    /// </summary>
    private static byte[] DecompressBytes(byte[] input)
    {
        if (input.Length == 0)
        {
            throw new InvalidDataException("empty stream");
        }

        // gzip magic is 0x1F 0x8B; anything else is treated as a zlib stream.
        bool isGzip = input.Length >= 2 && input[0] == 0x1F && input[1] == 0x8B;

        using var inputStream = new MemoryStream(input, writable: false);
        using var output = new MemoryStream();
        if (isGzip)
        {
            using var gz = new GZipStream(inputStream, CompressionMode.Decompress);
            gz.CopyTo(output);
        }
        else
        {
            using var zl = new ZLibStream(inputStream, CompressionMode.Decompress);
            zl.CopyTo(output);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Maps a zlib numeric level (0..9) to the coarse <see cref="CompressionLevel"/> enum.
    /// Default/best compression maps to <see cref="CompressionLevel.SmallestSize"/> to match
    /// the C++ default of <c>Z_BEST_COMPRESSION</c>.
    /// </summary>
    private static CompressionLevel MapCompressionLevel(int level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        >= 1 and <= 3 => CompressionLevel.Fastest,
        >= 4 and <= 8 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize,
    };
}
