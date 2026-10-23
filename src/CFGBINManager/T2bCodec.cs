using System.Buffers.Binary;

namespace CFGBINManager;

internal static class T2bCodec
{
    internal static CfgDocument Read(ReadOnlySpan<byte> source, StructureCatalog catalog)
    {
        if (source.Length < 48 || !source.Slice(source.Length - 16, 4).SequenceEqual(new byte[] { 1, 116, 50, 98 })) throw new InvalidDataException("Unknown format: expected T2B footer or RDBNP header.");
        int footer = source.Length - 16;
        if (source[footer + 4] != 254 || source[footer + 5] != 1) throw new InvalidDataException("Unsupported T2B footer profile.");
        // Span cannot be captured by local functions; this temporary input is discarded after parsing.
        byte[] sourceCopy = source.ToArray();
        int Read(int offset) => checked((int)BinaryTools.U32(sourceCopy, offset));
        int count = Read(0), stringOffset = Read(4), stringLength = Read(8), terminators = Read(12);
        BinaryTools.Range(source, stringOffset, stringLength, "T2B string pool");
        if (stringOffset < 16 || count > (stringOffset - 16) / 8) throw new InvalidDataException("T2B entries cannot fit before the strings.");
        int code = BinaryPrimitives.ReadUInt16LittleEndian(source[(footer + 6)..]);
        var encoding = BinaryTools.Encoding(code);
        var layout = new T2bLayout { EncodingCode = code, Footer = source[footer..].ToArray(), Strings = BinaryTools.ReadPool(source.Slice(stringOffset, stringLength), encoding) };
        if (layout.Strings.Count != terminators) throw new InvalidDataException("T2B physical string count differs from the header.");
        int nameBase = BinaryTools.Align(checked(stringOffset + stringLength), 16);
        BinaryTools.Range(source, nameBase, 16, "T2B name table");
        int sectionSize = Read(nameBase), nameCount = Read(nameBase + 4), namesAt = Read(nameBase + 8), namesLength = Read(nameBase + 12);
        if (checked(nameBase + sectionSize) != footer || namesAt < checked(16 + nameCount * 8) || namesAt > sectionSize - namesLength) throw new InvalidDataException("Invalid T2B name table envelope.");
        BinaryTools.Range(source, nameBase + 16, checked(nameCount * 8), "T2B name records");
        layout.Names = BinaryTools.ReadPool(source.Slice(nameBase + namesAt, namesLength), encoding);
        for (int i = 0; i < nameCount; i++)
        {
            var record = new NameRecord(BinaryTools.U32(source, nameBase + 16 + i * 8), Read(nameBase + 20 + i * 8));
            _ = BinaryTools.String(source.Slice(nameBase + namesAt, namesLength), record.Offset, encoding);
            layout.NameRecords.Add(record);
        }
        layout.PoolGap = source.Slice(stringOffset + stringLength, nameBase - stringOffset - stringLength).ToArray();
        layout.NameGap = source.Slice(nameBase + 16 + nameCount * 8, namesAt - 16 - nameCount * 8).ToArray();
        layout.NameTail = source.Slice(nameBase + namesAt + namesLength, sectionSize - namesAt - namesLength).ToArray();
        var namesBytes = source.Slice(nameBase + namesAt, namesLength);
        var names = new Dictionary<uint, List<string>>();
        foreach (NameRecord item in layout.NameRecords)
        {
            if (!names.TryGetValue(item.Hash, out var candidates)) names[item.Hash] = candidates = [];
            candidates.Add(BinaryTools.String(namesBytes, item.Offset, encoding));
        }
        var document = new CfgDocument(BinaryFormat.T2b, catalog) { T2b = layout, T2bInfo = new(stringOffset, stringLength, terminators, code) };
        int cursor = 16;
        for (int rowIndex = 0; rowIndex < count; rowIndex++)
        {
            BinaryTools.Range(source[..stringOffset], cursor, 5, $"T2B row {rowIndex}");
            uint hash = BinaryTools.U32(source, cursor); int valueCount = source[cursor + 4]; cursor += 5;
            int typeLength = (valueCount + 3) / 4;
            BinaryTools.Range(source[..stringOffset], cursor, typeLength, "T2B type bits");
            byte[] types = source.Slice(cursor, typeLength).ToArray(); cursor += typeLength;
            int aligned = BinaryTools.Align(cursor, 4);
            byte[] padding = source.Slice(cursor, aligned - cursor).ToArray(); cursor = aligned;
            BinaryTools.Range(source[..stringOffset], cursor, checked(valueCount * 4), $"T2B row {rowIndex} values");
            var values = new List<CfgValue>(); var offsets = new int[valueCount]; Array.Fill(offsets, -1);
            int valueOffset = cursor;
            var rawValues = new uint[valueCount];
            for (int j = 0; j < valueCount; j++, cursor += 4)
            {
                uint bits = BinaryTools.U32(source, cursor); int type = (types[j / 4] >> (j % 4 * 2)) & 3;
                rawValues[j] = bits;
                values.Add(type switch
                {
                    0 => CfgValue.String(bits == uint.MaxValue ? null : BinaryTools.String(source.Slice(stringOffset, stringLength), checked((int)bits), encoding)),
                    1 => CfgValue.Signed(unchecked((int)bits)),
                    2 => new CfgValue(ValueKind.Float32, bits, null, []),
                    _ => new CfgValue(ValueKind.Opaque, bits, null, [])
                });
                if (type == 0) offsets[j] = unchecked((int)bits);
            }
            string name = names.TryGetValue(hash, out var candidatesForHash) && candidatesForHash.Distinct().Count() == 1 ? candidatesForHash[0] : $"HASH_{hash:X8}";
            var row = new CfgRow(name, hash, Enumerable.Range(0, valueCount).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(), values) { TypeBytes = types, Padding = padding, StringOffsets = offsets };
            row.T2bInfo = new(valueOffset, Array.AsReadOnly(rawValues), names.TryGetValue(hash, out var resolved) ? resolved.AsReadOnly() : Array.Empty<string>());
            document.Attach(row); document.MutableRows.Add(row);
        }
        if (BinaryTools.Align(cursor, 16) != stringOffset) throw new InvalidDataException("T2B entry end does not match the native 32-bit profile.");
        layout.EntryGap = source.Slice(cursor, stringOffset - cursor).ToArray();
        return document;
    }
    internal static byte[] Write(CfgDocument document)
    {
        document.Catalog.ValidateLinks(document);
        var counters = document.Catalog.CalculateCounts(document);
        T2bLayout layout = document.T2b ?? new T2bLayout();
        var encoding = BinaryTools.Encoding(layout.EncodingCode);
        var strings = BinaryTools.WritePool(layout.Strings, encoding).ToList();
        var names = BinaryTools.WritePool(layout.Names, encoding).ToList();
        var records = layout.NameRecords.ToList();
        foreach (NameRecord record in records) _ = BinaryTools.String(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(names), record.Offset, encoding);
        var stringLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (PoolString item in layout.Strings) stringLookup.TryAdd(item.Text, item.Offset);
        int StringOffset(CfgRow row, int i)
        {
            string? text = row.Values[i].Text; if (text is null) return -1;
            if (text.Contains('\0')) throw new InvalidDataException("T2B strings cannot contain NUL.");
            int original = i < row.StringOffsets.Length ? row.StringOffsets[i] : -1;
            if (original >= 0 && original < strings.Count && BinaryTools.String(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(strings), original, encoding) == text) return original;
            if (stringLookup.TryGetValue(text, out int offset)) return offset;
            offset = strings.Count; strings.AddRange(encoding.GetBytes(text)); strings.Add(0); stringLookup.Add(text, offset); return offset;
        }
        using var entries = new MemoryStream();
        using var writer = new BinaryWriter(entries, encoding, true);
        foreach (CfgRow row in document.Rows)
        {
            int n = row.Values.Count;
            if (n > 255) throw new InvalidDataException($"T2B row {row.Name} exceeds 255 values.");
            if (row.Name.Contains('\0')) throw new InvalidDataException("T2B names cannot contain NUL.");
            var candidates = records.Where(record => record.Hash == row.NameHash).Select(record => BinaryTools.String(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(names), record.Offset, encoding)).Distinct(StringComparer.Ordinal).ToArray();
            if (candidates.Length > 0 && row.Name != $"HASH_{row.NameHash:X8}" && !candidates.Contains(row.Name, StringComparer.Ordinal)) throw new InvalidDataException($"Row name '{row.Name}' disagrees with native hash 0x{row.NameHash:X8}.");
            if (!records.Any(x => x.Hash == row.NameHash))
            {
                if (row.Name.StartsWith("HASH_", StringComparison.Ordinal)) { /* Unresolved identities need no invented name. */ }
                else
                {
                    if (row.NameHash != BinaryTools.Crc32(row.Name, encoding)) throw new InvalidDataException($"New T2B tag '{row.Name}' has an incorrect native CRC32.");
                    records.Add(new NameRecord(row.NameHash, names.Count)); names.AddRange(encoding.GetBytes(row.Name)); names.Add(0);
                }
            }
            writer.Write(row.NameHash); writer.Write((byte)n);
            byte[] types = row.TypeBytes.Length == (n + 3) / 4 ? row.TypeBytes.ToArray() : new byte[(n + 3) / 4];
            for (int i = 0; i < n; i++)
            {
                int code = row.Values[i].Kind switch { ValueKind.String => 0, ValueKind.Signed => 1, ValueKind.Float32 => 2, ValueKind.Opaque => 3, _ => throw new InvalidDataException($"{row.Name}[{i}] has unsupported T2B kind {row.Values[i].Kind}.") };
                types[i / 4] = (byte)((types[i / 4] & ~(3 << (i % 4 * 2))) | (code << (i % 4 * 2)));
            }
            writer.Write(types);
            Pad(writer, row.Padding, BinaryTools.Align(checked((int)entries.Length), 4) - checked((int)entries.Length), 0);
            for (int i = 0; i < n; i++)
            {
                var value = counters.GetValueOrDefault((row, i), row.Values[i]);
                uint bits = value.Kind switch { ValueKind.String => unchecked((uint)StringOffset(row, i)), ValueKind.Signed => unchecked((uint)checked((int)value.AsInt64())), _ => checked((uint)value.Bits) };
                writer.Write(bits);
            }
        }
        int entryEnd = checked(16 + (int)entries.Length);
        int stringAt = BinaryTools.Align(entryEnd, 16), nameAt = BinaryTools.Align(checked(stringAt + strings.Count), 16);
        bool originalNames = records.Count == layout.NameRecords.Count;
        int recordEnd = checked(16 + records.Count * 8);
        int nameTextAt = originalNames ? checked(recordEnd + layout.NameGap.Length) : BinaryTools.Align(recordEnd, 16);
        int nameTailLength = originalNames ? layout.NameTail.Length : BinaryTools.Align(checked(nameTextAt + names.Count), 16) - nameTextAt - names.Count;
        int nameSize = checked(nameTextAt + names.Count + nameTailLength);
        using var output = new MemoryStream(); using var result = new BinaryWriter(output, encoding, true);
        result.Write(document.Rows.Count); result.Write(stringAt); result.Write(strings.Count); result.Write(strings.Count(x => x == 0));
        result.Write(entries.ToArray()); Pad(result, layout.EntryGap, stringAt - entryEnd, 255);
        result.Write(strings.ToArray()); Pad(result, layout.PoolGap, nameAt - checked((int)output.Length), 255);
        result.Write(nameSize); result.Write(records.Count); result.Write(nameTextAt); result.Write(names.Count);
        foreach (NameRecord record in records) { result.Write(record.Hash); result.Write(record.Offset); }
        Pad(result, layout.NameGap, nameTextAt - recordEnd, 255); result.Write(names.ToArray()); Pad(result, layout.NameTail, nameTailLength, 255);
        if (layout.Footer.Length != 16 || !layout.Footer.AsSpan(0, 4).SequenceEqual(new byte[] { 1, 116, 50, 98 }) || layout.Footer[4] != 254 || layout.Footer[5] != 1) throw new InvalidDataException("T2B footer must contain 16 bytes.");
        byte[] footerBytes = layout.Footer.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(footerBytes.AsSpan(6), checked((ushort)layout.EncodingCode)); result.Write(footerBytes);
        return output.ToArray();
    }
    private static void Pad(BinaryWriter writer, byte[] original, int count, byte fill)
    {
        if (original.Length == count) writer.Write(original);
        else for (int i = 0; i < count; i++) writer.Write(fill);
    }
}
