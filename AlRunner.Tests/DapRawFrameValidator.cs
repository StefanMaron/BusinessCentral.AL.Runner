using System.Text;
using System.Text.Json;

namespace AlRunner.Tests;

/// <summary>
/// Strict, no-tolerance re-parser for the DAP wire format
/// (<c>Content-Length: N\r\n\r\n</c> + N bytes of JSON, repeated), independent of
/// <see cref="AlRunner.Infrastructure.DapTransport"/>'s own reader — for issue #2058's
/// acceptance criterion "no non-DAP byte reaches stdout in stdio mode", which
/// DapTransport itself cannot prove: its header loop silently SKIPS any header line
/// without a colon (see DapTransport.ReadMessageAsync's `if (idx &lt;= 0) continue;`)
/// rather than rejecting it, so a stray banner line ahead of "Content-Length:" would
/// pass DapTransport's own parse without complaint. This validator requires every
/// single byte of the input to belong to exactly one well-formed frame — nothing
/// before the first frame, nothing between frames, nothing after the last one — and
/// throws naming the offset the moment that is not true.
/// </summary>
public static class DapRawFrameValidator
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("Content-Length: ");

    /// <summary>Parses <paramref name="raw"/> as zero or more back-to-back DAP
    /// frames consuming every byte, and returns how many frames were found. Throws
    /// <see cref="InvalidDataException"/> naming the byte offset and a short dump of
    /// the surrounding bytes the moment anything does not match a well-formed frame
    /// exactly.</summary>
    public static int CountFramesOrThrow(byte[] raw)
    {
        var pos = 0;
        var count = 0;
        while (pos < raw.Length)
        {
            if (pos + Prefix.Length > raw.Length || !raw.AsSpan(pos, Prefix.Length).SequenceEqual(Prefix))
                throw new InvalidDataException(
                    $"stray bytes at offset {pos} (frame #{count + 1}): expected 'Content-Length: ', got {Dump(raw, pos)}");
            pos += Prefix.Length;

            var digitsStart = pos;
            while (pos < raw.Length && raw[pos] is >= (byte)'0' and <= (byte)'9') pos++;
            if (pos == digitsStart)
                throw new InvalidDataException($"no Content-Length digits at offset {digitsStart}: {Dump(raw, digitsStart)}");
            var contentLength = int.Parse(Encoding.ASCII.GetString(raw, digitsStart, pos - digitsStart));

            if (pos + 4 > raw.Length || raw[pos] != '\r' || raw[pos + 1] != '\n' || raw[pos + 2] != '\r' || raw[pos + 3] != '\n')
                throw new InvalidDataException($"expected CRLFCRLF right after Content-Length at offset {pos}: {Dump(raw, pos)}");
            pos += 4;

            if (pos + contentLength > raw.Length)
                throw new InvalidDataException(
                    $"frame #{count + 1} body truncated at offset {pos}: need {contentLength} bytes, only {raw.Length - pos} remain");

            // Must be valid, complete JSON occupying EXACTLY contentLength bytes — this
            // is what catches trailing garbage appended to a body without its own
            // Content-Length header (a corrupted length would show up here too).
            JsonDocument.Parse(raw.AsSpan(pos, contentLength).ToArray());
            pos += contentLength;
            count++;
        }
        return count;
    }

    private static string Dump(byte[] raw, int pos)
    {
        var len = Math.Min(40, raw.Length - pos);
        var slice = raw.AsSpan(pos, Math.Max(len, 0)).ToArray();
        return $"\"{Encoding.Latin1.GetString(slice)}\" ({len} bytes shown)";
    }
}
