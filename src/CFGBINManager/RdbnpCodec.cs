using System.Buffers.Binary;
using System.Text;

namespace CFGBINManager;

internal static class RdbnpCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static CfgDocument Read(ReadOnlySpan<byte> source, StructureCatalog catalog)
    {
        Bounds(source.Length, 0, 60, "header");
        if (!source[..5].SequenceEqual("RDBNP"u8)) throw Error("Invalid signature.");
        if (source[8] > 1) throw Error("Unknown byte order.");
        bool big = source[8] != 0;
        ushort version = U16(source, 6, big);
        if (version != 100) throw Error($"Unsupported version {version}; version 100 is required.");
        int dataBase = checked(U16(source, 10, big) * 4);
        var layout = new RdbLayout
        {
            Version = version, BigEndian = big, DataBase = dataBase,
            DataSize = Int(U32(source, 12, big)), Length = source.Length,
            TypeOffset = checked(dataBase + U16(source, 36, big) * 4),
            FieldOffset = checked(dataBase + U16(source, 40, big) * 4),
            RootOffset = checked(dataBase + U16(source, 44, big) * 4),
            HashOffset = checked(dataBase + U16(source, 48, big) * 4),
            NameOffset = checked(dataBase + U16(source, 50, big) * 4),
            ValueOffset = checked(dataBase + U16(source, 54, big) * 4),
            StringOffset = checked(dataBase + Int(U32(source, 56, big)))
        };
        if (dataBase < 60 || layout.DataSize != source.Length - dataBase || layout.StringOffset < layout.ValueOffset)
            throw Error("Invalid data size or section bounds.");
        Bounds(source.Length, dataBase, layout.DataSize, "data section");
        Bounds(source.Length, layout.StringOffset, 0, "string section");
        var occupied = new bool[source.Length];
        Mark(occupied, 0, 5, "signature"); Mark(occupied, 6, 3, "version and byte order");
        Mark(occupied, 10, 6, "data header"); Mark(occupied, 36, 24, "section header");
        // Keep each physical pool entry, including duplicates and padding empty strings.
        for (int p = layout.StringOffset; p < source.Length;)
        {
            int end = source[p..].IndexOf((byte)0);
            if (end < 0) break;
            byte[] encoded = source.Slice(p, end).ToArray();
            string text;
            try { text = Utf8.GetString(encoded); }
            catch (DecoderFallbackException exception) { throw Error($"Invalid UTF-8 in string pool at {p}.", exception); }
            layout.Strings.Add(new PoolString(p - layout.StringOffset, text, null));
            Mark(occupied, p, end + 1, "string pool"); p += end + 1;
        }
        int nameCount = U16(source, 52, big);
        for (int i = 0; i < nameCount; i++)
        {
            int h = checked(layout.HashOffset + i * 4), n = checked(layout.NameOffset + i * 4);
            Mark(occupied, h, 4, "name hashes"); Mark(occupied, n, 4, "name offsets");
            uint hash = U32(source, h, big); int offset = Int(U32(source, n, big));
            ReadString(source, layout.StringOffset, offset);
            layout.Names.Add(new NameRecord(hash, offset));
        }
        var names = new Dictionary<uint, string>();
        foreach (var group in layout.Names.GroupBy(x => x.Hash))
        {
            var candidateNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (NameRecord record in group) candidateNames.Add(ReadString(source, layout.StringOffset, record.Offset));
            string[] candidates = candidateNames.ToArray();
            names[group.Key] = candidates.Length == 1 ? candidates[0] : $"0x{group.Key:X8}";
        }
        string Resolve(uint hash) => names.GetValueOrDefault(hash, $"0x{hash:X8}");
        int typeCount = U16(source, 38, big), fieldCount = U16(source, 42, big), rootCount = U16(source, 46, big);
        for (int i = 0; i < fieldCount; i++)
        {
            int p = checked(layout.FieldOffset + i * 32); Mark(occupied, p, 32, "field descriptors");
            uint hash = U32(source, p, big);
            layout.Fields.Add(new RdbField { Hash = hash, Name = Resolve(hash), Code = U16(source, p + 4, big), Flags = U16(source, p + 6, big),
                Size = Int(U32(source, p + 8, big)), Offset = Int(U32(source, p + 12, big)), Count = Int(U32(source, p + 16, big)), Tail = source.Slice(p + 20, 12).ToArray() });
        }
        for (int i = 0; i < typeCount; i++)
        {
            int p = checked(layout.TypeOffset + i * 32); Mark(occupied, p, 32, "type descriptors");
            uint hash = U32(source, p, big);
            var type = new RdbType { Hash = hash, Name = Resolve(hash), LayoutId = U32(source, p + 4, big), FieldIndex = U16(source, p + 8, big), FieldCount = U16(source, p + 10, big), Tail = source.Slice(p + 12, 20).ToArray() };
            Bounds(fieldCount, type.FieldIndex, type.FieldCount, $"fields of {type.Name}"); layout.Types.Add(type);
        }
        var document = new CfgDocument(BinaryFormat.Rdbnp, catalog) { Rdb = layout };
        for (int i = 0; i < rootCount; i++)
        {
            int p = checked(layout.RootOffset + i * 32); Mark(occupied, p, 32, "root descriptors");
            var root = new RdbRoot { Type = U16(source, p, big), Flags = U16(source, p + 2, big), Offset = Int(U32(source, p + 4, big)), RowSize = Int(U32(source, p + 8, big)),
                RowCount = Int(U32(source, p + 12, big)), Hash = U32(source, p + 16, big), IndexOffset = Int(U32(source, p + 20, big)), IndexKind = U32(source, p + 24, big), Tail = U32(source, p + 28, big) };
            root.OriginalRowCount = root.RowCount;
            if (root.RowSize <= 0 && root.RowCount != 0) throw Error($"Invalid row size in root {i}.");
            int start = checked(layout.ValueOffset + root.Offset);
            int bytes = checked(root.RowSize * root.RowCount);
            Bounds(layout.StringOffset, start, bytes, $"rows of root {i}");
            var list = new CfgList { Name = Resolve(root.Hash), Layout = root, TypeName = (root.Flags & 1) != 0 ? Resolve(root.Hash) : layout.Types[root.Type].Name };
            for (int j = 0; j < root.RowCount; j++)
            {
                int rowStart = checked(start + j * root.RowSize);
                var rowUsed = new bool[root.RowSize];
                var fields = RootFields(layout, root, catalog);
                var values = new List<CfgValue>();
                foreach (var (field, offset) in fields)
                    values.Add(ReadField(source, rowStart + offset, root.RowSize - offset, field, layout, catalog, rowUsed, offset, new HashSet<int>()));
                string rowName = (root.Flags & 1) != 0 ? list.Name : layout.Types[root.Type].Name;
                var row = new CfgRow(rowName, (root.Flags & 1) != 0 ? root.Hash : layout.Types[root.Type].Hash, fields.Select(x => x.Field.Name).ToArray(), values);
                row.RdbPadding = GetGaps(source.Slice(rowStart, root.RowSize), rowUsed);
                document.Attach(row); list.MutableRows.Add(row);
                Mark(occupied, rowStart, root.RowSize, "root rows");
            }
            if (root.IndexOffset != 0)
            {
                int width = IndexWidth(root.IndexKind), indexStart = checked(layout.ValueOffset + root.IndexOffset);
                Bounds(layout.StringOffset, indexStart, checked(width * root.RowCount), "auxiliary index");
                Mark(occupied, indexStart, checked(width * root.RowCount), "auxiliary index");
                for (int j = 0; j < root.RowCount; j++) root.Index.Add(width == 2 ? U16(source, indexStart + j * width, big) : U32(source, indexStart + j * width, big));
                if (!root.Index.Order().SequenceEqual(Enumerable.Range(0, root.RowCount).Select(x => (uint)x))) throw Error($"Auxiliary index for {list.Name} is not a permutation.");
                root.IndexKey = catalog.GetIndexKey(list.Name) ?? InferIndexKey(list, root.Index);
                if (root.IndexKey is not null) root.OriginalIndexKeys = list.Rows.Select(row => Key(row, root.IndexKey)).ToList();
            }
            root.OriginalRowsFingerprint = RowsFingerprint(list);
            layout.Roots.Add(root); document.MutableLists.Add(list);
        }
        layout.Gaps = GetGaps(source, occupied);
        ValidateSchemaFlags(layout);
        layout.SchemaFingerprint = SchemaFingerprint(layout);
        ValidateReferences(document);
        return document;
    }

    internal static byte[] Write(CfgDocument document)
    {
        RdbLayout layout = document.Rdb ?? throw Error("RDBNP layout is missing.");
        if (layout.Version != 100) throw Error($"Unsupported version {layout.Version}.");
        ValidateSchemaFlags(layout);
        if (string.IsNullOrEmpty(layout.SchemaFingerprint) || layout.SchemaFingerprint != SchemaFingerprint(layout))
            throw Error("Native schema descriptors were modified; their layout identifiers cannot be regenerated.");
        if (document.Lists.Count != layout.Roots.Count) throw Error("Adding or removing root schemas is unsupported.");
        if (layout.DataBase < 60 || layout.DataBase % 4 != 0 || layout.ValueOffset < layout.DataBase || layout.StringOffset < layout.ValueOffset || layout.Length < layout.StringOffset || layout.DataSize != layout.Length - layout.DataBase)
            throw Error("Invalid RDBNP section layout.");
        foreach (CfgList list in document.Lists)
        {
            RdbRoot root = list.Layout ?? throw Error($"Missing root layout for {list.Name}.");
            string nativeName = ResolveNativeName(layout, root.Hash);
            if (list.Name != nativeName) throw Error($"List '{list.Name}' does not match native root identity '{nativeName}'.");
        }
        ValidateReferences(document);
        int cursor = layout.StringOffset;
        var plans = new List<(CfgList List, RdbRoot Root, int Rows, int Index, List<uint> Order)>();
        foreach (CfgList list in document.Lists)
        {
            RdbRoot root = list.Layout ?? throw Error($"Missing layout for list {list.Name}.");
            if (!layout.Roots.Contains(root)) throw Error($"List {list.Name} has an unknown schema.");
            if (root.RowSize < 0 || root.RowCount < 0 || root.OriginalRowCount < 0) throw Error($"Negative row size or count in {list.Name}.");
            int rowStart = checked(layout.ValueOffset + root.Offset), indexStart = root.IndexOffset == 0 ? 0 : checked(layout.ValueOffset + root.IndexOffset);
            bool resized = list.Rows.Count != root.RowCount;
            if (resized) { cursor = Align(cursor, 16); rowStart = cursor; cursor = checked(cursor + list.Rows.Count * root.RowSize); }
            List<uint> order = GetIndexOrder(document, list, root);
            if (root.IndexOffset != 0 && resized) { cursor = Align(cursor, 4); indexStart = cursor; cursor = checked(cursor + list.Rows.Count * IndexWidth(root.IndexKind)); }
            plans.Add((list, root, rowStart, indexStart, order));
        }
        int newStrings = cursor;
        var pool = new List<byte>();
        foreach (PoolString item in layout.Strings)
        {
            byte[] encoded = item.Encoded ?? Utf8.GetBytes(item.Text);
            try
            {
                if (Utf8.GetString(encoded) != item.Text) throw Error("String pool text disagrees with its retained encoding.");
            }
            catch (DecoderFallbackException exception) { throw Error($"Invalid retained UTF-8 at pool offset {item.Offset}.", exception); }
            Bounds(layout.Length - layout.StringOffset, item.Offset, checked(encoded.Length + 1), "pool string");
            int end = checked(item.Offset + encoded.Length + 1);
            while (pool.Count < end) pool.Add(0);
            for (int i = 0; i < encoded.Length; i++) pool[item.Offset + i] = encoded[i];
        }
        int originalPoolLength = layout.Length - layout.StringOffset;
        while (pool.Count < originalPoolLength) pool.Add(0);
        foreach (OpaqueRegion gap in layout.Gaps.Where(x => x.Offset >= layout.StringOffset))
        {
            int at = gap.Offset - layout.StringOffset;
            Bounds(originalPoolLength, at, gap.Bytes.Length, "opaque string tail");
            for (int i = 0; i < gap.Bytes.Length; i++) pool[at + i] = gap.Bytes[i];
        }
        foreach (var plan in plans)
            foreach (CfgRow row in plan.List.Rows)
                foreach (CfgValue value in row.Values) PrepareStrings(value, pool);
        int length = checked(newStrings + pool.Count);
        ValidateOutputLayout(layout, plans, newStrings, pool, length, document.Catalog);
        var output = new byte[length];
        pool.CopyTo(output, newStrings);
        foreach (OpaqueRegion gap in layout.Gaps)
        {
            int destination = gap.Offset >= layout.StringOffset ? checked(gap.Offset + newStrings - layout.StringOffset) : gap.Offset;
            Bounds(output.Length, destination, gap.Bytes.Length, "opaque gap"); gap.Bytes.CopyTo(output, destination);
        }
        "RDBNP"u8.CopyTo(output); bool big = layout.BigEndian;
        W16(output, 6, layout.Version, big); output[8] = big ? (byte)1 : (byte)0;
        W16(output, 10, checked((ushort)(layout.DataBase / 4)), big); W32(output, 12, checked((uint)(length - layout.DataBase)), big);
        W16(output, 36, SectionWord(layout.TypeOffset, layout), big); W16(output, 38, checked((ushort)layout.Types.Count), big);
        W16(output, 40, SectionWord(layout.FieldOffset, layout), big); W16(output, 42, checked((ushort)layout.Fields.Count), big);
        W16(output, 44, SectionWord(layout.RootOffset, layout), big); W16(output, 46, checked((ushort)layout.Roots.Count), big);
        W16(output, 48, SectionWord(layout.HashOffset, layout), big); W16(output, 50, SectionWord(layout.NameOffset, layout), big);
        W16(output, 52, checked((ushort)layout.Names.Count), big); W16(output, 54, SectionWord(layout.ValueOffset, layout), big);
        W32(output, 56, checked((uint)(newStrings - layout.DataBase)), big);
        for (int i = 0; i < layout.Types.Count; i++)
        {
            RdbType type = layout.Types[i]; int p = checked(layout.TypeOffset + i * 32);
            W32(output, p, type.Hash, big); W32(output, p + 4, type.LayoutId, big); W16(output, p + 8, checked((ushort)type.FieldIndex), big); W16(output, p + 10, checked((ushort)type.FieldCount), big);
            CopyTail(type.Tail, output, p + 12, 20);
        }
        for (int i = 0; i < layout.Fields.Count; i++)
        {
            RdbField field = layout.Fields[i]; int p = checked(layout.FieldOffset + i * 32);
            W32(output, p, field.Hash, big); W16(output, p + 4, field.Code, big); W16(output, p + 6, field.Flags, big);
            W32(output, p + 8, checked((uint)field.Size), big); W32(output, p + 12, checked((uint)field.Offset), big); W32(output, p + 16, checked((uint)field.Count), big); CopyTail(field.Tail, output, p + 20, 12);
        }
        for (int i = 0; i < layout.Names.Count; i++)
        {
            W32(output, layout.HashOffset + i * 4, layout.Names[i].Hash, big); W32(output, layout.NameOffset + i * 4, checked((uint)layout.Names[i].Offset), big);
        }
        foreach (var plan in plans)
        {
            int ri = layout.Roots.IndexOf(plan.Root), p = checked(layout.RootOffset + ri * 32); RdbRoot root = plan.Root;
            W16(output, p, root.Type, big); W16(output, p + 2, root.Flags, big); W32(output, p + 4, checked((uint)(plan.Rows - layout.ValueOffset)), big);
            W32(output, p + 8, checked((uint)root.RowSize), big); W32(output, p + 12, checked((uint)plan.List.Rows.Count), big); W32(output, p + 16, root.Hash, big);
            W32(output, p + 20, plan.Index == 0 ? 0 : checked((uint)(plan.Index - layout.ValueOffset)), big); W32(output, p + 24, root.IndexKind, big); W32(output, p + 28, root.Tail, big);
            var fields = RootFields(layout, root, document.Catalog);
            for (int j = 0; j < plan.List.Rows.Count; j++)
            {
                CfgRow row = plan.List.Rows[j]; int rowStart = checked(plan.Rows + j * root.RowSize);
                if (row.Values.Count != fields.Count) throw Error($"Field count changed in {plan.List.Name}[{j}].");
                foreach (OpaqueRegion gap in row.RdbPadding) { Bounds(root.RowSize, gap.Offset, gap.Bytes.Length, "row padding"); gap.Bytes.CopyTo(output, rowStart + gap.Offset); }
                var used = new bool[root.RowSize];
                for (int k = 0; k < fields.Count; k++)
                {
                    var (field, offset) = fields[k];
                    WriteField(output, rowStart + offset, root.RowSize - offset, field, row.Values[k], layout, document.Catalog, pool, used, offset, new HashSet<int>());
                }
            }
            if (plan.Index != 0)
                for (int j = 0; j < plan.Order.Count; j++)
                    if (IndexWidth(root.IndexKind) == 2) W16(output, plan.Index + j * 2, checked((ushort)plan.Order[j]), big); else W32(output, plan.Index + j * 4, plan.Order[j], big);
        }
        return output;
    }

    private static string ResolveNativeName(RdbLayout layout, uint hash)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (NameRecord record in layout.Names.Where(x => x.Hash == hash))
        {
            bool found = false;
            foreach (PoolString item in layout.Strings)
            {
                byte[] encoded = item.Encoded ?? Utf8.GetBytes(item.Text);
                long relative = (long)record.Offset - item.Offset;
                if (relative < 0 || relative > encoded.Length) continue;
                try { names.Add(Utf8.GetString(encoded.AsSpan((int)relative))); }
                catch (DecoderFallbackException exception) { throw Error($"Invalid UTF-8 name at pool offset {record.Offset}.", exception); }
                found = true; break;
            }
            if (!found) throw Error($"Name offset {record.Offset} does not identify a retained pool string.");
        }
        return names.Count == 1 ? names.Single() : $"0x{hash:X8}";
    }

    private static void ValidateSchemaFlags(RdbLayout layout)
    {
        foreach (RdbField field in layout.Fields)
            if (field.Flags is not (1 or 2 or 3)) throw Error($"Unknown flags {field.Flags} for field {field.Name}.");
        foreach (RdbRoot root in layout.Roots)
            if (root.Flags is not (1 or 2 or 3)) throw Error($"Unknown flags {root.Flags} for root 0x{root.Hash:X8}.");
    }

    private static string SchemaFingerprint(RdbLayout layout)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(layout.Types.Count);
            foreach (RdbType type in layout.Types)
            {
                writer.Write(type.Hash); writer.Write(type.Name); writer.Write(type.LayoutId); writer.Write(type.FieldIndex); writer.Write(type.FieldCount);
                writer.Write(type.Tail.Length); writer.Write(type.Tail);
            }
            writer.Write(layout.Fields.Count);
            foreach (RdbField field in layout.Fields)
            {
                writer.Write(field.Hash); writer.Write(field.Name); writer.Write(field.Code); writer.Write(field.Flags); writer.Write(field.Size); writer.Write(field.Offset); writer.Write(field.Count);
                writer.Write(field.Tail.Length); writer.Write(field.Tail);
            }
            writer.Write(layout.Roots.Count);
            foreach (RdbRoot root in layout.Roots)
            {
                writer.Write(root.Type); writer.Write(root.Flags); writer.Write(root.RowSize); writer.Write(root.Hash); writer.Write(root.IndexKind); writer.Write(root.Tail);
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void ValidateOutputLayout(RdbLayout layout, List<(CfgList List, RdbRoot Root, int Rows, int Index, List<uint> Order)> plans, int strings, List<byte> pool, int length, StructureCatalog catalog)
    {
        var occupied = new bool[length];
        Mark(occupied, 0, 5, "signature"); Mark(occupied, 6, 3, "version and byte order");
        Mark(occupied, 10, 6, "data header"); Mark(occupied, 36, 24, "section header");
        void Table(int at, int count, int width, string context)
        {
            Bounds(layout.ValueOffset, at, checked(count * width), context);
            if (at < layout.DataBase) throw Error($"{context} precedes the data section.");
            Mark(occupied, at, checked(count * width), context);
        }
        Table(layout.TypeOffset, layout.Types.Count, 32, "type descriptors");
        Table(layout.FieldOffset, layout.Fields.Count, 32, "field descriptors");
        Table(layout.RootOffset, layout.Roots.Count, 32, "root descriptors");
        Table(layout.HashOffset, layout.Names.Count, 4, "name hashes");
        Table(layout.NameOffset, layout.Names.Count, 4, "name offsets");
        foreach (var plan in plans)
        {
            RdbRoot root = plan.Root;
            int originalStart = checked(layout.ValueOffset + root.Offset);
            if (root.RowSize <= 0 && (root.RowCount != 0 || plan.List.Rows.Count != 0)) throw Error("Invalid root row size.");
            if (originalStart < layout.ValueOffset) throw Error("Root rows precede the value section.");
            Bounds(layout.StringOffset, originalStart, checked(root.RowSize * root.RowCount), "original root rows");
            Mark(occupied, originalStart, checked(root.RowSize * root.RowCount), "original root rows");
            if (plan.Rows != originalStart) Mark(occupied, plan.Rows, checked(root.RowSize * plan.List.Rows.Count), "relocated root rows");
            if (root.IndexOffset != 0)
            {
                int oldIndex = checked(layout.ValueOffset + root.IndexOffset), width = IndexWidth(root.IndexKind);
                Bounds(layout.StringOffset, oldIndex, checked(root.RowCount * width), "original auxiliary index");
                Mark(occupied, oldIndex, checked(root.RowCount * width), "original auxiliary index");
                if (plan.Index != oldIndex) Mark(occupied, plan.Index, checked(plan.List.Rows.Count * width), "relocated auxiliary index");
            }
            var fields = RootFields(layout, root, catalog);
            var typed = new bool[root.RowSize];
            foreach (var (field, offset) in fields) MarkFieldStorage(layout, catalog, field, root.RowSize - offset, typed, offset, []);
            foreach (CfgRow row in plan.List.Rows)
            {
                var rowOccupied = (bool[])typed.Clone();
                foreach (OpaqueRegion gap in row.RdbPadding) Mark(rowOccupied, gap.Offset, gap.Bytes.Length, "opaque row padding");
            }
        }
        int originalPoolLength = layout.Length - layout.StringOffset;
        foreach (PoolString item in layout.Strings)
        {
            if (item.Text.Contains('\0')) throw Error("Pool strings cannot contain NUL.");
            int size = checked((item.Encoded?.Length ?? Utf8.GetByteCount(item.Text)) + 1);
            Bounds(originalPoolLength, item.Offset, size, "pool string");
            Mark(occupied, checked(strings + item.Offset), size, "pool string");
        }
        Mark(occupied, checked(strings + originalPoolLength), pool.Count - originalPoolLength, "new pool strings");
        foreach (NameRecord name in layout.Names)
        {
            if (name.Offset < 0) throw Error("Negative name offset.");
            ReadString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pool), 0, name.Offset);
        }
        foreach (OpaqueRegion gap in layout.Gaps)
        {
            Bounds(layout.Length, gap.Offset, gap.Bytes.Length, "original opaque gap");
            if (gap.Offset < layout.StringOffset && gap.Bytes.Length > layout.StringOffset - gap.Offset)
                throw Error("Opaque gap crosses the string section.");
            int at = gap.Offset >= layout.StringOffset ? checked(gap.Offset + strings - layout.StringOffset) : gap.Offset;
            Mark(occupied, at, gap.Bytes.Length, "opaque gap");
        }
    }

    private static void MarkFieldStorage(RdbLayout layout, StructureCatalog catalog, RdbField field, int available, bool[] occupied, int offset, HashSet<int> nesting)
    {
        Bounds(available, 0, checked(field.Size * field.Count), field.Name);
        for (int i = 0; i < field.Count; i++)
        {
            int element = checked(offset + i * field.Size);
            if ((field.Flags & 1) != 0)
            {
                int width = PrimitiveSize(field.Code); Bounds(field.Size, 0, width, field.Name);
                Mark(occupied, element, width, field.Name);
            }
            else
            {
                if (field.Code >= layout.Types.Count || !nesting.Add(field.Code)) throw Error($"Invalid or recursive nested type in {field.Name}.");
                foreach (var (child, childOffset) in TypeFields(layout, field.Code, catalog))
                    MarkFieldStorage(layout, catalog, child, field.Size - childOffset, occupied, checked(element + childOffset), nesting);
                nesting.Remove(field.Code);
            }
        }
    }

    internal static List<(RdbField Field, int Offset)> RootFields(RdbLayout layout, RdbRoot root, StructureCatalog catalog)
    {
        if ((root.Flags & 1) != 0)
            return [(new RdbField { Name = "value", Code = root.Type, Flags = 1, Count = 1, Size = PrimitiveSize(root.Type) }, 0)];
        if (root.Type >= layout.Types.Count) throw Error($"Nested root type {root.Type} is out of bounds.");
        return TypeFields(layout, root.Type, catalog);
    }

    internal static List<(RdbField Field, int Offset)> TypeFields(RdbLayout layout, int typeIndex, StructureCatalog catalog)
    {
        RdbType type = layout.Types[typeIndex];
        Bounds(layout.Fields.Count, type.FieldIndex, type.FieldCount, $"fields of {type.Name}");
        var result = new List<(RdbField, int)>(); int delta = 0;
        for (int i = 0; i < type.FieldCount; i++)
        {
            RdbField field = layout.Fields[type.FieldIndex + i];
            if (field.Count < 1 || field.Size < 1 || field.Offset < 0) throw Error($"Invalid descriptor for {type.Name}.{field.Name}.");
            int alignment = (field.Flags & 1) == 0 ? 4 : field.Code switch { 18 or 19 => 16, 5 or 9 or 21 => 2, 3 or 4 or 8 => 1, _ => 4 };
            int offset = catalog.GetLayoutOffset(type.Name, type.LayoutId, field.Name) ?? Align(checked(field.Offset + delta), alignment);
            if (offset < 0) throw Error($"Negative layout offset for {type.Name}.{field.Name}.");
            delta = checked(offset - field.Offset + field.Size * (field.Count - 1));
            result.Add((field, offset));
        }
        return result;
    }

    private static CfgValue ReadField(ReadOnlySpan<byte> source, int position, int available, RdbField field, RdbLayout layout, StructureCatalog catalog, bool[] used, int usedOffset, HashSet<int> nesting)
    {
        Bounds(available, 0, checked(field.Size * field.Count), $"field {field.Name}");
        if (field.Count > 1)
        {
            var items = new List<CfgValue>();
            for (int i = 0; i < field.Count; i++) items.Add(ReadElement(source, checked(position + i * field.Size), field, layout, catalog, used, checked(usedOffset + i * field.Size), nesting));
            return new CfgValue(ValueKind.Array, 0, null, items);
        }
        return ReadElement(source, position, field, layout, catalog, used, usedOffset, nesting);
    }

    private static CfgValue ReadElement(ReadOnlySpan<byte> source, int position, RdbField field, RdbLayout layout, StructureCatalog catalog, bool[] used, int usedOffset, HashSet<int> nesting)
    {
        if ((field.Flags & 1) == 0)
        {
            if (field.Code >= layout.Types.Count || !nesting.Add(field.Code)) throw Error($"Invalid or recursive nested type in {field.Name}.");
            var values = new List<CfgValue>();
            foreach (var (child, offset) in TypeFields(layout, field.Code, catalog))
                values.Add(ReadField(source, checked(position + offset), field.Size - offset, child, layout, catalog, used, checked(usedOffset + offset), nesting));
            nesting.Remove(field.Code);
            return new CfgValue(ValueKind.Structure, 0, null, values);
        }
        int width = PrimitiveSize(field.Code);
        Bounds(field.Size, 0, width, $"primitive {field.Name}"); Mark(used, usedOffset, width, field.Name);
        bool big = layout.BigEndian;
        ulong bits = width switch { 1 => source[position], 2 => U16(source, position, big), 4 => U32(source, position, big), 8 => U64(source, position, big), _ => 0 };
        switch (field.Code)
        {
            case 3: return new CfgValue(ValueKind.Boolean, bits, null, []);
            case 4: case 5: case 6: case 7: return CfgValue.Unsigned(bits);
            case 8: return CfgValue.Signed(unchecked((sbyte)bits));
            case 9: return CfgValue.Signed(unchecked((short)bits));
            case 10: return CfgValue.Signed(unchecked((int)bits));
            case 11: return CfgValue.Signed(unchecked((long)bits));
            case 13: return new CfgValue(ValueKind.Float32, bits, null, []);
            case 14: return new CfgValue(ValueKind.Float64, bits, null, []);
            case 15: return new CfgValue(ValueKind.Hash32, bits, null, []);
            case 16: return new CfgValue(ValueKind.Hash64, bits, null, []);
            case 17: case 18: case 19:
                var components = new List<CfgValue>(); int floats = field.Code == 17 ? 2 : field.Code == 18 ? 3 : 4;
                for (int i = 0; i < floats; i++) components.Add(new CfgValue(ValueKind.Float32, U32(source, position + i * 4, big), null, []));
                if (field.Code == 18) components.Add(CfgValue.Unsigned(U32(source, position + 12, big)));
                return new CfgValue(ValueKind.Vector, 0, null, components);
            case 20:
                int stringOffset = unchecked((int)bits);
                if (stringOffset < -1) throw Error($"Invalid string offset {stringOffset} for {field.Name}.");
                return new CfgValue(ValueKind.String, unchecked((ulong)(long)stringOffset), stringOffset == -1 ? null : ReadString(source, layout.StringOffset, stringOffset), []);
            case 21: return CfgValue.Range16(U16(source, position, big), U16(source, position + 2, big));
            case 22: return CfgValue.Range32(U32(source, position, big), U32(source, position + 4, big));
            default: throw Error($"Unregistered primitive {field.Code}.");
        }
    }

    private static void WriteField(byte[] output, int position, int available, RdbField field, CfgValue value, RdbLayout layout, StructureCatalog catalog, List<byte> pool, bool[] used, int usedOffset, HashSet<int> nesting)
    {
        Bounds(available, 0, checked(field.Size * field.Count), $"field {field.Name}");
        if (field.Count > 1)
        {
            Require(value, ValueKind.Array, field.Name);
            if (value.Items.Count != field.Count) throw Error($"Array {field.Name} requires {field.Count} elements.");
            for (int i = 0; i < field.Count; i++) WriteElement(output, checked(position + i * field.Size), field, value.Items[i], layout, catalog, pool, used, checked(usedOffset + i * field.Size), nesting);
        }
        else WriteElement(output, position, field, value, layout, catalog, pool, used, usedOffset, nesting);
    }

    private static void WriteElement(byte[] output, int position, RdbField field, CfgValue value, RdbLayout layout, StructureCatalog catalog, List<byte> pool, bool[] used, int usedOffset, HashSet<int> nesting)
    {
        if ((field.Flags & 1) == 0)
        {
            Require(value, ValueKind.Structure, field.Name);
            if (field.Code >= layout.Types.Count || !nesting.Add(field.Code)) throw Error($"Invalid or recursive nested type in {field.Name}.");
            var children = TypeFields(layout, field.Code, catalog);
            if (value.Items.Count != children.Count) throw Error($"Nested structure {field.Name} has an invalid field count.");
            for (int i = 0; i < children.Count; i++)
            {
                var (child, offset) = children[i];
                WriteField(output, checked(position + offset), field.Size - offset, child, value.Items[i], layout, catalog, pool, used, checked(usedOffset + offset), nesting);
            }
            nesting.Remove(field.Code); return;
        }
        int width = PrimitiveSize(field.Code); Bounds(field.Size, 0, width, field.Name); Mark(used, usedOffset, width, field.Name);
        ValueKind kind = field.Code switch
        {
            3 => ValueKind.Boolean, >= 4 and <= 7 => ValueKind.Unsigned, >= 8 and <= 11 => ValueKind.Signed,
            13 => ValueKind.Float32, 14 => ValueKind.Float64, 15 => ValueKind.Hash32, 16 => ValueKind.Hash64,
            >= 17 and <= 19 => ValueKind.Vector, 20 => ValueKind.String, 21 => ValueKind.Reference16, 22 => ValueKind.Reference32,
            _ => throw Error($"Unregistered primitive {field.Code}.")
        };
        Require(value, kind, field.Name); bool big = layout.BigEndian;
        if (kind == ValueKind.Vector)
        {
            int expected = field.Code == 17 ? 2 : 4;
            if (value.Items.Count != expected) throw Error($"Vector {field.Name} requires {expected} stored components.");
            for (int i = 0; i < expected; i++)
            {
                Require(value.Items[i], field.Code == 18 && i == 3 ? ValueKind.Unsigned : ValueKind.Float32, field.Name);
                W32(output, position + i * 4, checked((uint)value.Items[i].Bits), big);
            }
            return;
        }
        if (kind == ValueKind.String) { W32(output, position, value.Text is null ? uint.MaxValue : checked((uint)StringOffset(value, pool)), big); return; }
        if (kind == ValueKind.Reference16)
        {
            if (value.Bits > uint.MaxValue) throw Error($"REF16 overflow in {field.Name}.");
            W16(output, position, (ushort)value.Bits, big); W16(output, position + 2, (ushort)(value.Bits >> 16), big); return;
        }
        if (kind == ValueKind.Reference32) { W32(output, position, (uint)value.Bits, big); W32(output, position + 4, (uint)(value.Bits >> 32), big); return; }
        ulong bits = value.Bits;
        if (kind == ValueKind.Signed)
        {
            long number = unchecked((long)bits);
            switch (width)
            {
                case 1: bits = unchecked((byte)checked((sbyte)number)); break;
                case 2: bits = unchecked((ushort)checked((short)number)); break;
                case 4: bits = unchecked((uint)checked((int)number)); break;
            }
        }
        switch (width)
        {
            case 1: output[position] = checked((byte)bits); break;
            case 2: W16(output, position, checked((ushort)bits), big); break;
            case 4: W32(output, position, checked((uint)bits), big); break;
            case 8: W64(output, position, bits, big); break;
        }
    }

    internal static int PrimitiveSize(int code) => code switch
    {
        3 or 4 or 8 => 1, 5 or 9 => 2, 6 or 10 or 13 or 15 or 20 or 21 => 4,
        7 or 11 or 14 or 16 or 17 or 22 => 8, 18 or 19 => 16,
        _ => throw Error($"Unregistered primitive code {code}.")
    };

    private static void Require(CfgValue value, ValueKind kind, string context)
    {
        if (value.Kind != kind) throw Error($"{context} requires {kind}, received {value.Kind}.");
    }

    private static void PrepareStrings(CfgValue value, List<byte> pool)
    {
        if (value.Kind == ValueKind.String && value.Text is not null) StringOffset(value, pool);
        foreach (CfgValue child in value.Items) PrepareStrings(child, pool);
    }

    private static int StringOffset(CfgValue value, List<byte> pool)
    {
        string text = value.Text ?? throw Error("Absent strings have no pool offset.");
        if (text.Contains('\0')) throw Error("Resource strings cannot contain NUL.");
        byte[] encoded = Utf8.GetBytes(text);
        long preferred = unchecked((long)value.Bits);
        bool Matches(int at)
        {
            if (at < 0 || at > pool.Count - encoded.Length - 1 || pool[at + encoded.Length] != 0) return false;
            for (int i = 0; i < encoded.Length; i++) if (pool[at + i] != encoded[i]) return false;
            return true;
        }
        if (preferred >= 0 && preferred <= int.MaxValue && Matches((int)preferred)) return (int)preferred;
        for (int i = 0; i <= pool.Count - encoded.Length - 1; i++) if (Matches(i)) return i;
        int offset = pool.Count; pool.AddRange(encoded); pool.Add(0); return offset;
    }

    private static string ReadString(ReadOnlySpan<byte> source, int stringBase, int relative)
    {
        int start = checked(stringBase + relative); Bounds(source.Length, start, 1, "string offset");
        int size = source[start..].IndexOf((byte)0);
        if (size < 0) throw Error($"Unterminated UTF-8 string at {start}.");
        try { return Utf8.GetString(source.Slice(start, size)); }
        catch (DecoderFallbackException exception) { throw Error($"Invalid UTF-8 string at {start}.", exception); }
    }

    private static List<OpaqueRegion> GetGaps(ReadOnlySpan<byte> source, bool[] used)
    {
        var gaps = new List<OpaqueRegion>();
        for (int i = 0; i < used.Length;)
        {
            if (used[i]) { i++; continue; }
            int start = i; while (i < used.Length && !used[i]) i++;
            gaps.Add(new OpaqueRegion(start, source.Slice(start, i - start).ToArray()));
        }
        return gaps;
    }

    private static void Mark(bool[] occupied, int position, int size, string context)
    {
        Bounds(occupied.Length, position, size, context);
        for (int i = position; i < position + size; i++)
        {
            if (occupied[i]) throw Error($"Overlapping {context} at byte {i}.");
            occupied[i] = true;
        }
    }

    private static void Bounds(int length, int offset, int size, string context)
    {
        if (offset < 0 || size < 0 || offset > length - size) throw Error($"{context} is outside its containing section (offset {offset}, size {size}, length {length}).");
    }
    private static int Int(uint number) => number <= int.MaxValue ? (int)number : throw Error($"Offset or count {number} exceeds supported bounds.");
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static ushort SectionWord(int position, RdbLayout layout)
    {
        int delta = checked(position - layout.DataBase);
        if (delta < 0 || delta % 4 != 0) throw Error("Section is not aligned to its data base.");
        return checked((ushort)(delta / 4));
    }
    private static void CopyTail(byte[] tail, byte[] output, int at, int size)
    {
        if (tail.Length != size) throw Error("Descriptor tail has an invalid size.");
        Bounds(output.Length, at, size, "descriptor tail"); tail.CopyTo(output, at);
    }
    private static int IndexWidth(uint kind) => kind switch { 1 => 2, 2 => 4, _ => throw Error($"Unknown auxiliary index kind {kind}.") };
    private static InvalidDataException Error(string message) => new($"[RDBNP] {message}");
    private static InvalidDataException Error(string message, Exception inner) => new($"[RDBNP] {message}", inner);
    private static ushort U16(ReadOnlySpan<byte> b, int p, bool big) { Bounds(b.Length, p, 2, "UInt16"); return big ? BinaryPrimitives.ReadUInt16BigEndian(b[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); }
    private static uint U32(ReadOnlySpan<byte> b, int p, bool big) { Bounds(b.Length, p, 4, "UInt32"); return big ? BinaryPrimitives.ReadUInt32BigEndian(b[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); }
    private static ulong U64(ReadOnlySpan<byte> b, int p, bool big) { Bounds(b.Length, p, 8, "UInt64"); return big ? BinaryPrimitives.ReadUInt64BigEndian(b[p..]) : BinaryPrimitives.ReadUInt64LittleEndian(b[p..]); }
    private static void W16(byte[] b, int p, ushort value, bool big) { Bounds(b.Length, p, 2, "UInt16"); if (big) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), value); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), value); }
    private static void W32(byte[] b, int p, uint value, bool big) { Bounds(b.Length, p, 4, "UInt32"); if (big) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), value); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), value); }
    private static void W64(byte[] b, int p, ulong value, bool big) { Bounds(b.Length, p, 8, "UInt64"); if (big) BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(p), value); else BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p), value); }

    private static string? InferIndexKey(CfgList list, IReadOnlyList<uint> order)
    {
        if (list.Rows.Count < 2) return null;
        var candidates = new List<string>();
        for (int field = 0; field < list.Rows[0].Values.Count; field++)
        {
            if (!IsKey(list.Rows[0].Values[field])) continue;
            ulong[] values = list.Rows.Select(row => SortKey(row.Values[field])).ToArray();
            if (values.Distinct().Take(2).Count() < 2) continue;
            ulong[] sorted = order.Select(i => values[checked((int)i)]).ToArray();
            if (sorted.SequenceEqual(sorted.Order())) candidates.Add(list.Rows[0].FieldNames[field]);
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
    private static bool IsKey(CfgValue value) => value.Kind is ValueKind.Signed or ValueKind.Unsigned or ValueKind.Hash32 or ValueKind.Hash64 or ValueKind.Boolean;
    private static ulong SortKey(CfgValue value) => value.Kind == ValueKind.Signed ? value.Bits ^ (1UL << 63) : IsKey(value) ? value.Bits : throw Error($"{value.Kind} cannot be used as an auxiliary index key.");
    private static ulong Key(CfgRow row, string field) => SortKey(row.GetValue(field));
    private static List<uint> GetIndexOrder(CfgDocument document, CfgList list, RdbRoot root)
    {
        if (root.IndexOffset == 0) return [];
        IndexWidth(root.IndexKind);
        if (!root.Index.Order().SequenceEqual(Enumerable.Range(0, root.RowCount).Select(x => (uint)x)))
            throw Error($"Original auxiliary index for {list.Name} is not a permutation.");
        string? field = document.Catalog.GetIndexKey(list.Name) ?? root.IndexKey;
        if (field is null)
        {
            if (list.Rows.Count != root.OriginalRowCount || RowsFingerprint(list) != root.OriginalRowsFingerprint)
                throw Error($"An INDEX_KEY declaration is required to edit indexed list {list.Name}; its original key is ambiguous.");
            return root.Index.ToList();
        }
        ulong[] keys = list.Rows.Select(row => Key(row, field)).ToArray();
        if (keys.SequenceEqual(root.OriginalIndexKeys) && root.Index.Count == keys.Length) return root.Index.ToList();
        return Enumerable.Range(0, keys.Length).OrderBy(i => keys[i]).Select(i => (uint)i).ToList();
    }

    private static string RowsFingerprint(CfgList list)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(CfgValue value)
        {
            Span<byte> header = stackalloc byte[17]; header[0] = (byte)value.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(header[1..], value.Bits);
            BinaryPrimitives.WriteInt32LittleEndian(header[9..], value.Items.Count);
            byte[]? text = value.Text is null ? null : Utf8.GetBytes(value.Text);
            BinaryPrimitives.WriteInt32LittleEndian(header[13..], text?.Length ?? -1);
            hash.AppendData(header); if (text is not null) hash.AppendData(text);
            foreach (CfgValue child in value.Items) Add(child);
        }
        foreach (CfgRow row in list.Rows) foreach (CfgValue value in row.Values) Add(value);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateReferences(CfgDocument document)
    {
        foreach (CfgList list in document.Lists)
        {
            RdbRoot root = list.Layout ?? throw Error($"Missing root layout for {list.Name}.");
            var fields = RootFields(document.Rdb!, root, document.Catalog);
            foreach (CfgRow row in list.Rows)
            {
                string expectedName = (root.Flags & 1) != 0 ? list.Name : document.Rdb!.Types[root.Type].Name;
                uint expectedHash = (root.Flags & 1) != 0 ? root.Hash : document.Rdb!.Types[root.Type].Hash;
                if (row.Name != expectedName || row.NameHash != expectedHash || row.Values.Count != fields.Count || !row.FieldNames.SequenceEqual(fields.Select(x => x.Field.Name)))
                    throw Error($"Row schema mismatch in {list.Name}.");
                for (int i = 0; i < fields.Count; i++)
                    TransformReferences(document, row.Name, fields[i].Field, row.Values[i], (type, field, value) =>
                    {
                        ValidateReference(document, type, field, value, null);
                        return value;
                    });
            }
        }
    }

    private static void ValidateReference(CfgDocument document, string type, string field, CfgValue value, CfgList? insertingInto)
    {
        ReferenceRule? rule = document.Catalog.References.SingleOrDefault(r => r.Type == type && r.Field == field);
        if (rule is null) return;
        CfgList? target = document.Lists.SingleOrDefault(x => x.Name == rule.Target);
        if (target is null) throw Error($"Reference target {rule.Target} for {type}.{field} is absent.");
        var (start, count) = value.AsRange();
        ulong rowCount = (ulong)target.Rows.Count + (target == insertingInto ? 1UL : 0UL);
        if (count != 0 && (ulong)start + count > rowCount)
            throw Error($"Reference {type}.{field} range {start}+{count} exceeds {target.Name} ({rowCount} rows).");
    }

    internal static void InsertRow(CfgDocument document, CfgList list, int index, CfgRow row)
    {
        if ((uint)index > (uint)list.Rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (row.Document is not null) throw new ArgumentException("The inserted row already belongs to a document. Create a new row to insert it.", nameof(row));
        if (list.Rows.Contains(row)) throw new ArgumentException("A row object cannot be inserted twice into the same list.", nameof(row));
        RdbRoot root = list.Layout ?? throw Error("List layout is missing.");
        var fields = RootFields(document.Rdb!, root, document.Catalog);
        string expectedName = (root.Flags & 1) != 0 ? list.Name : document.Rdb!.Types[root.Type].Name;
        uint expectedHash = (root.Flags & 1) != 0 ? root.Hash : document.Rdb!.Types[root.Type].Hash;
        if (row.Name != expectedName || row.NameHash != expectedHash || row.Values.Count != fields.Count || !row.FieldNames.SequenceEqual(fields.Select(x => x.Field.Name)))
            throw Error($"Inserted row does not match schema {expectedName}.");
        ValidateReferences(document);
        var changes = RebaseReferences(document, list, index, true);
        // Validate the new row before applying any mutations to existing rows.
        var buffer = new byte[root.RowSize];
        var used = new bool[root.RowSize];
        var pool = new List<byte>();
        for (int i = 0; i < fields.Count; i++)
            WriteField(buffer, fields[i].Offset, root.RowSize - fields[i].Offset, fields[i].Field, row.Values[i], document.Rdb!, document.Catalog, pool, used, fields[i].Offset, []);
        for (int i = 0; i < fields.Count; i++)
            TransformReferences(document, row.Name, fields[i].Field, row.Values[i], (type, field, value) =>
            {
                ValidateReference(document, type, field, value, list);
                return value;
            });
        foreach (var change in changes) change.Row.Assign(change.Field, change.Value);
        document.Attach(row); list.MutableRows.Insert(index, row);
    }

    internal static void RemoveRow(CfgDocument document, CfgList list, int index)
    {
        if ((uint)index >= (uint)list.Rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        ValidateReferences(document);
        var changes = RebaseReferences(document, list, index, false);
        foreach (var change in changes) change.Row.Assign(change.Field, change.Value);
        CfgRow row = list.MutableRows[index]; list.MutableRows.RemoveAt(index); row.Document = null;
    }

    private static List<(CfgRow Row, int Field, CfgValue Value)> RebaseReferences(CfgDocument document, CfgList target, int index, bool insert)
    {
        var changes = new List<(CfgRow, int, CfgValue)>();
        foreach (CfgList list in document.Lists)
        {
            var fields = RootFields(document.Rdb!, list.Layout!, document.Catalog);
            foreach (CfgRow row in list.Rows)
            {
                if (!insert && list == target && ReferenceEquals(row, target.Rows[index])) continue;
                for (int i = 0; i < fields.Count; i++)
                {
                    CfgValue original = row.Values[i];
                    CfgValue changed = TransformReferences(document, row.Name, fields[i].Field, original, (type, field, value) =>
                    {
                        var (start, count) = value.AsRange();
                        if (count == 0) return value;
                        ReferenceRule? rule = document.Catalog.References.SingleOrDefault(r => r.Type == type && r.Field == field);
                        if (rule is null) throw Error($"Cannot edit {target.Name}: nonempty incoming reference {type}.{field} has no REFERENCE declaration.");
                        if (rule.Target != target.Name) return value;
                        ulong end = (ulong)start + count;
                        if (insert)
                        {
                            if ((uint)index < start) start = checked(start + 1);
                            else if ((ulong)index < end) count = checked(count + 1);
                        }
                        else
                        {
                            if ((uint)index < start) start--;
                            else if ((ulong)index < end) count--;
                        }
                        return value.Kind == ValueKind.Reference16 ? CfgValue.Range16(checked((ushort)start), checked((ushort)count)) : CfgValue.Range32(start, count);
                    });
                    if (!ReferenceEquals(original, changed)) changes.Add((row, i, changed));
                }
            }
        }
        RdbRoot root = target.Layout!;
        if (root.IndexOffset != 0 && document.Catalog.GetIndexKey(target.Name) is null && root.IndexKey is null)
            throw Error($"An INDEX_KEY declaration is required to change row count in {target.Name}.");
        if (insert && root.IndexOffset != 0 && root.IndexKind == 1 && target.Rows.Count >= ushort.MaxValue + 1)
            throw Error($"Auxiliary UInt16 index in {target.Name} cannot represent another row.");
        return changes;
    }

    private static CfgValue TransformReferences(CfgDocument document, string typeName, RdbField field, CfgValue value, Func<string, string, CfgValue, CfgValue> transform)
    {
        if (field.Count > 1)
        {
            Require(value, ValueKind.Array, field.Name);
            if (value.Items.Count != field.Count) throw Error($"Array {field.Name} requires {field.Count} values.");
            var element = new RdbField { Name = field.Name, Code = field.Code, Flags = field.Flags, Size = field.Size, Count = 1 };
            CfgValue[] items = value.Items.Select(x => TransformReferences(document, typeName, element, x, transform)).ToArray();
            return items.Where((x, i) => !ReferenceEquals(x, value.Items[i])).Any() ? value with { Items = items } : value;
        }
        if ((field.Flags & 1) == 0)
        {
            Require(value, ValueKind.Structure, field.Name);
            if (field.Code >= document.Rdb!.Types.Count) throw Error($"Invalid nested type in {field.Name}.");
            RdbType type = document.Rdb.Types[field.Code];
            var fields = TypeFields(document.Rdb, field.Code, document.Catalog);
            if (value.Items.Count != fields.Count) throw Error($"Nested structure {field.Name} has an invalid field count.");
            CfgValue[] items = value.Items.Select((x, i) => TransformReferences(document, type.Name, fields[i].Field, x, transform)).ToArray();
            return items.Where((x, i) => !ReferenceEquals(x, value.Items[i])).Any() ? value with { Items = items } : value;
        }
        return field.Code is 21 or 22 ? transform(typeName, field.Name, value) : value;
    }
}
