using System.Buffers.Binary;
using System.Text;

namespace CFGBINManager;

internal static class BinaryTools
{
    internal static int Align(int n, int alignment) => checked((n + alignment - 1) & -alignment);
    internal static void Range(ReadOnlySpan<byte> source, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > source.Length - length) throw new InvalidDataException($"{context}: range {offset}+{length} exceeds {source.Length} bytes.");
    }
    internal static uint U32(ReadOnlySpan<byte> data, int offset) { Range(data, offset, 4, "UInt32"); return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]); }
    internal static void U32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    internal static Encoding Encoding(int code)
    {
        if (code == 1) return new UTF8Encoding(false, true);
        if (code != 0) throw new InvalidDataException($"Unsupported T2B encoding code {code}.");
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    internal static string String(ReadOnlySpan<byte> data, int offset, Encoding encoding)
    {
        Range(data, offset, 1, "String"); int count = data[offset..].IndexOf((byte)0);
        if (count < 0) throw new InvalidDataException($"Unterminated string at {offset}.");
        return encoding.GetString(data.Slice(offset, count));
    }
    internal static List<PoolString> ReadPool(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var pool = new List<PoolString>(); int offset = 0;
        while (offset < bytes.Length)
        {
            int length = bytes[offset..].IndexOf((byte)0);
            if (length < 0) throw new InvalidDataException("A physical string pool is not terminated.");
            var raw = bytes.Slice(offset, length); string text = encoding.GetString(raw);
            pool.Add(new PoolString(offset, text, raw.SequenceEqual(encoding.GetBytes(text)) ? null : raw.ToArray()));
            offset += length + 1;
        }
        return pool;
    }
    internal static byte[] WritePool(IEnumerable<PoolString> pool, Encoding encoding)
    {
        using var output = new MemoryStream();
        foreach (PoolString value in pool)
        {
            if (value.Offset != output.Length) throw new InvalidDataException("Physical pool string offsets are not contiguous.");
            if (value.Text.Contains('\0') || value.Encoded is not null && (value.Encoded.Contains((byte)0) || encoding.GetString(value.Encoded) != value.Text)) throw new InvalidDataException("Physical pool text disagrees with its encoding or contains NUL.");
            output.Write(value.Encoded ?? encoding.GetBytes(value.Text)); output.WriteByte(0);
        }
        return output.ToArray();
    }
    internal static uint Crc32(string text, Encoding encoding)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in encoding.GetBytes(text)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0); }
        return ~crc;
    }
}
