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

    public static CfgDocument CreateT2b(StructureCatalog catalog, int valueWidth, int encodingCode, bool useJamCrc, IEnumerable<CfgRow> rows)
    {
        if (valueWidth is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(valueWidth), "T2B value width must be 4 or 8 bytes.");
        ArgumentNullException.ThrowIfNull(rows);
        _ = BinaryTools.Encoding(encodingCode);
        var document = CreateT2b(catalog);
        document.T2b!.ValueWidth = valueWidth;
        document.T2b.EncodingCode = encodingCode;
        document.T2b.UseJamCrc = useJamCrc;
        foreach (var row in rows)
        {
            if (row is null) throw new ArgumentException("T2B records cannot be null.", nameof(rows));
            var copy = document.CreateT2bRow(row.Name, row.Values);
            bool raw = row.Name.Length == 13 && row.Name.StartsWith("HASH_", StringComparison.Ordinal)
                && uint.TryParse(row.Name.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash) && hash == row.NameHash;
            if (raw) copy.NameHash = row.NameHash;
            else if (row.NameHash != 0 && row.NameHash != copy.NameHash)
                throw new ArgumentException($"T2B record '{row.Name}' does not match its name hash.", nameof(rows));
            document.Attach(copy);
            document.MutableRows.Add(copy);
        }
        document.Catalog.ValidateLinks(document);
        return document;
    }

    public CfgRow CreateT2bRow(string name, IReadOnlyList<CfgValue> values)
    {
        RequireT2b();
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0')) throw new ArgumentException("Enter a nonempty record name without NUL.", nameof(name));
        if (values.Count > 255) throw new ArgumentException("T2B records support at most 255 values.", nameof(values));
        foreach (var value in values) ValidateT2bValue(value);
        var encoding = BinaryTools.Encoding(T2b?.EncodingCode ?? 1);
        _ = encoding.GetBytes(name);
        return new CfgRow(name, BinaryTools.Crc32(name, encoding, T2b?.UseJamCrc ?? false), Enumerable.Range(0, values.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(), values);
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
            if (record.Hash != BinaryTools.Crc32(text, originalEncoding, layout.UseJamCrc)) throw new InvalidDataException("Cannot convert a name table containing nonstandard hashes.");
            if (!nameOffsets.TryGetValue(record.Offset, out int offset))
            {
                offset = names.Sum(item => encoding.GetByteCount(item.Text) + 1);
                names.Add(new PoolString(offset, text, null)); nameOffsets.Add(record.Offset, offset);
            }
            records.Add(new NameRecord(BinaryTools.Crc32(text, encoding, layout.UseJamCrc), offset));
        }
        foreach (var row in copy.Rows)
        {
            if (row.Name == $"HASH_{row.NameHash:X8}") throw new InvalidDataException("Cannot convert text encoding while record names are unresolved.");
            row.NameHash = BinaryTools.Crc32(row.Name, encoding, layout.UseJamCrc);
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

    private void ValidateT2bValue(CfgValue value)
    {
        int width = T2bValueWidth!.Value;
        if (T2bCodec.TypeCode(value, width) < 0)
            throw new ArgumentException($"The T2B {width * 8}-bit profile does not support {value.Kind} fields.", nameof(value));
        if (value.Kind == ValueKind.String && value.Text?.Contains('\0') == true) throw new ArgumentException("Strings cannot contain NUL.", nameof(value));
        if (width == 4 && value.Kind == ValueKind.Signed) _ = checked((int)value.AsInt64());
        else if (width == 4 && value.Kind != ValueKind.String) _ = checked((uint)value.Bits);
    }
}
