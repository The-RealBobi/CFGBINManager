namespace CFGBINManager;

public static class BinCodec
{
    public static CfgDocument Read(ReadOnlySpan<byte> bytes, StructureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.RequireProfileSelection();
        try
        {
            if (bytes.StartsWith("\u001bLua"u8)) throw new InvalidDataException("Lua bytecode is not a CFG record format.");
            return bytes.StartsWith("RDBNP"u8) ? RdbnpCodec.Read(bytes, catalog) : T2bCodec.Read(bytes, catalog);
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("The binary contains an invalid range, dimension or encoded string.", error); }
    }
    public static byte[] Write(CfgDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Format == BinaryFormat.Rdbnp) return RdbnpCodec.Write(document);
        return T2bCodec.Write(document);
    }

    public static byte[] WriteWithFixedStringEdits(CfgDocument document, IReadOnlyList<CfgStringEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edits);
        if (document.Format != BinaryFormat.T2b) throw new ArgumentException("Fixed string allocations require T2B.", nameof(document));
        byte[] result = Write(document);
        CfgDocument rebuilt = Read(result, document.Catalog);
        T2bDocumentInfo info = rebuilt.T2bInfo ?? throw new InvalidDataException("Missing T2B source metadata.");
        var encoding = BinaryTools.Encoding(info.EncodingCode);
        var locations = new HashSet<(int, int)>();
        var allocations = new List<(int Start, int End, byte[] Bytes)>();
        foreach (CfgStringEdit edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if ((uint)edit.RowIndex >= rebuilt.Rows.Count) throw new ArgumentOutOfRangeException(nameof(edits), "Row index is outside the document.");
            CfgRow row = rebuilt.Rows[edit.RowIndex];
            if ((uint)edit.ValueIndex >= row.Values.Count) throw new ArgumentOutOfRangeException(nameof(edits), "Value index is outside the row.");
            if (!locations.Add((edit.RowIndex, edit.ValueIndex))) throw new ArgumentException("A string can be edited only once per operation.", nameof(edits));
            CfgValue value = row.Values[edit.ValueIndex];
            if (value.Kind != ValueKind.String || value.Text is null || edit.Text is null || edit.Text.Contains('\0')) throw new ArgumentException("Fixed edits require existing non-null strings and NUL-free replacements.", nameof(edits));
            int start = row.StringOffsets[edit.ValueIndex];
            int length = result.AsSpan(info.StringOffset + start, info.StringLength - start).IndexOf((byte)0);
            if (length < 0) throw new InvalidDataException("String allocation is not terminated.");
            byte[] replacement = encoding.GetBytes(edit.Text);
            if (replacement.Length > length) throw new ArgumentException("Replacement exceeds its existing encoded allocation.", nameof(edits));
            foreach (CfgRow other in rebuilt.Rows)
                for (int i = 0; i < other.Values.Count; i++)
                {
                    if (other == row && i == edit.ValueIndex || other.Values[i].Kind != ValueKind.String || other.Values[i].Text is null) continue;
                    int otherStart = other.StringOffsets[i];
                    int otherEnd = checked(otherStart + encoding.GetByteCount(other.Values[i].Text!));
                    if (otherStart <= start + length && otherEnd >= start) throw new InvalidOperationException("The string allocation is shared or overlaps another string reference.");
                }
            allocations.Add((start, start + length, replacement));
        }
        foreach (var allocation in allocations)
        {
            Span<byte> target = result.AsSpan(info.StringOffset + allocation.Start, allocation.End - allocation.Start);
            target.Fill((byte)' ');
            allocation.Bytes.CopyTo(target);
        }
        _ = Read(result, document.Catalog);
        return result;
    }
}
