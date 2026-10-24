using System.Globalization;

namespace CFGBINManager;

public sealed partial class CfgDocument
{
    public static CfgDocument CreateT2b(StructureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.RequireProfileSelection();
        return new CfgDocument(BinaryFormat.T2b, catalog) { T2b = new T2bLayout() };
    }

    public CfgRow CreateT2bRow(string name, IReadOnlyList<CfgValue> values)
    {
        RequireT2b();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new ArgumentException("Enter a nonempty record name without NUL.", nameof(name));
        if (values.Count > 255) throw new ArgumentException("T2B records support at most 255 values.", nameof(values));
        foreach (var value in values) ValidateT2bValue(value);
        var encoding = BinaryTools.Encoding(T2b?.EncodingCode ?? 1);
        _ = encoding.GetBytes(name);
        return new CfgRow(name, BinaryTools.Crc32(name, encoding), Enumerable.Range(0, values.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(), values);
    }

    public void SetT2bValue(CfgRow row, int index, CfgValue value)
    {
        RequireT2bRow(row); ValidateT2bValue(value);
        row.Assign(index, value);
        row.StringOffsets[index] = -1;
    }

    public void InsertT2bValue(CfgRow row, int index, CfgValue value)
    {
        RequireT2bRow(row); ValidateT2bValue(value);
        if (row.Values.Count == 255) throw new InvalidOperationException("T2B records support at most 255 values.");
        if ((uint)index > row.Values.Count) throw new ArgumentOutOfRangeException(nameof(index));
        row.ResizeValues(index, value);
    }

    public void RemoveT2bValue(CfgRow row, int index)
    {
        RequireT2bRow(row);
        if ((uint)index >= row.Values.Count) throw new ArgumentOutOfRangeException(nameof(index));
        row.ResizeValues(index, null);
    }

    public CfgDocument ConvertTextEncoding(int code)
    {
        RequireT2b();
        var encoding = BinaryTools.Encoding(code);
        CfgDocument copy = CfgTextCodec.Read(CfgTextCodec.Write(this), Catalog);
        var layout = copy.T2b!;
        if (layout.EncodingCode == code) return copy;
        var originalEncoding = BinaryTools.Encoding(layout.EncodingCode);
        byte[] oldNames = BinaryTools.WritePool(layout.Names, originalEncoding);
        var nameOffsets = new Dictionary<int, int>();
        List<PoolString> Reencode(IReadOnlyList<PoolString> pool, bool names)
        {
            var result = new List<PoolString>(); int offset = 0;
            foreach (var item in pool)
            {
                if (names) nameOffsets.Add(item.Offset, offset);
                result.Add(new PoolString(offset, item.Text, null));
                offset = checked(offset + encoding.GetByteCount(item.Text) + 1);
            }
            return result;
        }
        layout.Strings = Reencode(layout.Strings, false);
        var names = Reencode(layout.Names, true);
        var records = new List<NameRecord>();
        foreach (var record in layout.NameRecords)
        {
            string text = BinaryTools.String(oldNames, record.Offset, originalEncoding);
            if (record.Hash != BinaryTools.Crc32(text, originalEncoding)) throw new InvalidDataException("Cannot convert a name table containing nonstandard hashes.");
            if (!nameOffsets.TryGetValue(record.Offset, out int offset))
            {
                offset = names.Sum(item => encoding.GetByteCount(item.Text) + 1);
                names.Add(new PoolString(offset, text, null)); nameOffsets.Add(record.Offset, offset);
            }
            records.Add(new NameRecord(BinaryTools.Crc32(text, encoding), offset));
        }
        foreach (var row in copy.Rows)
        {
            if (row.Name.StartsWith("HASH_", StringComparison.Ordinal)) throw new InvalidDataException("Cannot convert text encoding while record names are unresolved.");
            row.NameHash = BinaryTools.Crc32(row.Name, encoding);
            Array.Fill(row.StringOffsets, -1);
        }
        layout.Names = names; layout.NameRecords = records; layout.EncodingCode = code;
        return BinCodec.Read(BinCodec.Write(copy), Catalog);
    }

    private void RequireT2b()
    {
        if (Format != BinaryFormat.T2b) throw new InvalidOperationException("RDBNP types and fields are fixed by its native schema.");
    }

    private void RequireT2bRow(CfgRow row)
    {
        RequireT2b();
        if (row.Document != this) throw new ArgumentException("The record must belong to this document.", nameof(row));
    }

    private static void ValidateT2bValue(CfgValue value)
    {
        if (value.Kind is not (ValueKind.String or ValueKind.Signed or ValueKind.Float32 or ValueKind.Opaque)) throw new ArgumentException("T2B supports String, Signed, Float32 and Opaque values.", nameof(value));
        if (value.Kind == ValueKind.String && value.Text?.Contains('\0') == true) throw new ArgumentException("Strings cannot contain NUL.", nameof(value));
        if (value.Kind == ValueKind.Signed) _ = checked((int)value.AsInt64());
        else if (value.Kind != ValueKind.String) _ = checked((uint)value.Bits);
    }
}
