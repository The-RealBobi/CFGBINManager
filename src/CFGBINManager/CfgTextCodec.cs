using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CFGBINManager;

public static partial class CfgTextCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new() { RespectNullableAnnotations = true };
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static string Write(CfgDocument document) => WriteCore(document, null);
    public static string WriteFragment(CfgDocument document, IReadOnlyList<CfgRow> rows)
    {
        var selected = rows.ToHashSet();
        if (selected.Count == 0 || selected.Any(row => row.Document != document)) throw new ArgumentException("Select records belonging to this document.", nameof(rows));
        return "// Record fragment; import into a matching document.\n" + WriteCore(document, selected);
    }
    private static string WriteCore(CfgDocument document, HashSet<CfgRow>? selected)
    {
        if (document.Format == BinaryFormat.T2b) document.Catalog.ValidateLinks(document);
        var counters = document.Format == BinaryFormat.T2b ? document.Catalog.CalculateCounts(document) : new Dictionary<(CfgRow Row, int Position), CfgValue>();
        var output = new StringBuilder();
        void Meta<T>(string name, T value) => output.Append("// @").Append(name).Append(' ').Append(CfgSyntax.Quote(JsonSerializer.Serialize(value, JsonOptions))).AppendLine(";");
        output.Append("// @FORMAT ").Append(CfgSyntax.Quote(document.Format == BinaryFormat.T2b ? "T2B" : "RDBNP")).AppendLine(", 1;");
        if (document.Format == BinaryFormat.T2b)
        {
            Meta("T2B_LAYOUT", selected is null ? document.T2b ?? new T2bLayout() : new T2bLayout { EncodingCode = document.TextEncodingCode ?? 1 });
            foreach (CfgRow row in document.Rows.Where(row => selected is null || selected.Contains(row))) WriteRow(row);
        }
        else
        {
            var layout = document.Rdb ?? throw new InvalidDataException("RDBNP document has no native schema.");
            if (selected is not null)
            {
                layout = JsonSerializer.Deserialize<RdbLayout>(JsonSerializer.Serialize(layout, JsonOptions), JsonOptions)!;
                layout.Strings.Clear(); layout.Names.Clear(); layout.Gaps.Clear(); layout.HeaderExtra = [];
                foreach (var root in layout.Roots)
                {
                    root.Index.Clear(); root.OriginalIndexKeys.Clear(); root.RowCount = root.OriginalRowCount = 0;
                    root.OriginalRowsFingerprint = "";
                }
            }
            Meta("RDB_LAYOUT", layout);
            for (int i = 0; i < document.Lists.Count; i++)
            {
                output.Append("// @LIST ").Append(i).Append(", ").Append(CfgSyntax.Quote(document.Lists[i].Name)).AppendLine(";");
                foreach (CfgRow row in document.Lists[i].Rows.Where(row => selected is null || selected.Contains(row))) WriteRow(row);
            }
        }
        return output.ToString();
        void WriteRow(CfgRow row)
        {
            Meta("ROW", new RowLayout(row.NameHash, row.FieldNames.ToArray(), row.Padding, row.TypeBytes,
                selected is null ? row.StringOffsets : Enumerable.Repeat(-1, row.Values.Count).ToArray(), row.RdbPadding, row.Values.Select(Shape).ToArray()));
            output.Append(IsIdentifier(row.Name) ? row.Name : CfgSyntax.Quote(row.Name));
            var atoms = row.Values.Select((value, index) => counters.GetValueOrDefault((row, index), value)).SelectMany(Flatten).ToArray();
            if (atoms.Length != 0) output.Append(' ').AppendJoin(", ", atoms.Select(Print));
            output.AppendLine(";");
        }
    }
    public static CfgDocument Read(string text, StructureCatalog catalog)
    {
        catalog.RequireProfileSelection();
        try { return ReadCore(ExpandReadable(text), catalog); }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new InvalidDataException("Invalid CFG metadata or value: " + error.Message, error); }
    }
    private static CfgDocument ReadCore(string text, StructureCatalog catalog)
    {
        var statements = CfgSyntax.Parse(text);
        var format = statements.Where(x => x.Metadata && x.Name == "FORMAT").ToArray();
        if (format.Length > 1) throw new InvalidDataException("Duplicate CFG format declaration.");
        BinaryFormat kind = BinaryFormat.T2b;
        if (format.Length == 1)
        {
            var v = format[0].Values;
            if (v.Count != 2 || CfgSyntax.Integer(v[1]) != 1) throw new InvalidDataException("Unsupported CFG text contract version.");
            kind = v[0].Text switch { "T2B" => BinaryFormat.T2b, "RDBNP" => BinaryFormat.Rdbnp, _ => throw new InvalidDataException("Unknown CFG format.") };
        }
        var document = new CfgDocument(kind, catalog);
        RowLayout? rowLayout = null; CfgList? list = null; bool hasLayout = false;
        foreach (Statement statement in statements)
        {
            if (statement.Metadata)
            {
                string Json() => statement.Values.Count == 1 && statement.Values[0].Quoted ? statement.Values[0].Text : throw new InvalidDataException($"{statement.Name} requires a quoted metadata object.");
                switch (statement.Name)
                {
                    case "FORMAT": break;
                    case "T2B_LAYOUT":
                        if (kind != BinaryFormat.T2b || hasLayout) throw new InvalidDataException("Unexpected or duplicate T2B layout.");
                        document.T2b = JsonSerializer.Deserialize<T2bLayout>(Json(), JsonOptions) ?? throw new InvalidDataException("Missing T2B layout."); hasLayout = true; break;
                    case "RDB_LAYOUT":
                        if (kind != BinaryFormat.Rdbnp || hasLayout) throw new InvalidDataException("Unexpected or duplicate RDBNP layout.");
                        document.Rdb = JsonSerializer.Deserialize<RdbLayout>(Json(), JsonOptions) ?? throw new InvalidDataException("Missing RDBNP layout."); hasLayout = true; break;
                    case "LIST":
                        if (document.Rdb is null || statement.Values.Count != 2) throw new InvalidDataException("LIST requires an RDBNP schema and index/name.");
                        int index = checked((int)CfgSyntax.Integer(statement.Values[0]));
                        if (index != document.Lists.Count || index >= document.Rdb.Roots.Count) throw new InvalidDataException("RDBNP list declarations must follow native root order.");
                        var root = document.Rdb.Roots[index];
                        list = new CfgList { Name = statement.Values[1].Text, Layout = root, TypeName = (root.Flags & 1) != 0 ? statement.Values[1].Text : document.Rdb.Types[root.Type].Name }; document.MutableLists.Add(list); break;
                    case "ROW":
                        if (rowLayout is not null) throw new InvalidDataException("ROW metadata is missing its content statement.");
                        rowLayout = JsonSerializer.Deserialize<RowLayout>(Json(), JsonOptions) ?? throw new InvalidDataException("Missing ROW metadata."); break;
                    default: throw new InvalidDataException($"Unknown CFG metadata '{statement.Name}'.");
                }
                continue;
            }
            string name = catalog.ResolveEntry(kind, statement.Name);
            CfgRow row;
            if (rowLayout is null)
            {
                if (kind != BinaryFormat.T2b) throw new InvalidDataException("RDBNP text requires native layout and field metadata; use the lossless CFG export.");
                var values = statement.Values.Select(ParsePlain).ToArray();
                var encoding = BinaryTools.Encoding(document.T2b?.EncodingCode ?? 1);
                uint hash = name.StartsWith("HASH_", StringComparison.Ordinal) && uint.TryParse(name.AsSpan(5), NumberStyles.HexNumber, Invariant, out uint raw) ? raw : BinaryTools.Crc32(name, encoding);
                row = new CfgRow(name, hash, Enumerable.Range(0, values.Length).Select(i => i.ToString(Invariant)).ToArray(), values);
            }
            else
            {
                int cursor = 0;
                var values = rowLayout.Values.Select(ReadShape).ToArray();
                if (cursor != statement.Values.Count) throw new InvalidDataException($"{name}: more content values than declared fields.");
                row = new CfgRow(name, rowLayout.Hash, rowLayout.Fields, values) { Padding = rowLayout.Padding, TypeBytes = rowLayout.Types, StringOffsets = rowLayout.StringOffsets, RdbPadding = rowLayout.RdbPadding };
                CfgValue ReadShape(ValueShape shape)
                {
                    if (shape.Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector) return new CfgValue(shape.Kind, 0, null, shape.Items.Select(ReadShape).ToArray());
                    if (cursor >= statement.Values.Count) throw new InvalidDataException($"{name}: missing content value.");
                    Token token = statement.Values[cursor++];
                    if (shape.Kind == ValueKind.String)
                    {
                        if (!token.Quoted && token.Text != "null") throw new InvalidDataException($"{name}: expected quoted string or null.");
                        return new CfgValue(shape.Kind, shape.StringOffset, token.Quoted ? token.Text : null, []);
                    }
                    if (token.Quoted) throw new InvalidDataException($"{name}: numeric field received a string.");
                    if (shape.Kind is ValueKind.Reference16 or ValueKind.Reference32)
                    {
                        if (cursor >= statement.Values.Count) throw new InvalidDataException($"{name}: reference count missing.");
                        ulong first = ParseUnsigned(token), count = ParseUnsigned(statement.Values[cursor++]);
                        return shape.Kind == ValueKind.Reference16 ? CfgValue.Range16(checked((ushort)first), checked((ushort)count)) : CfgValue.Range32(checked((uint)first), checked((uint)count));
                    }
                    ulong bits = shape.Kind switch
                    {
                        ValueKind.Float32 => token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? checked((uint)ParseUnsigned(token)) : BitConverter.SingleToUInt32Bits(float.Parse(token.Text, NumberStyles.Float, Invariant)),
                        ValueKind.Float64 => token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ParseUnsigned(token) : BitConverter.DoubleToUInt64Bits(double.Parse(token.Text, NumberStyles.Float, Invariant)),
                        ValueKind.Signed => unchecked((ulong)long.Parse(token.Text, NumberStyles.Integer, Invariant)),
                        _ => ParseUnsigned(token)
                    };
                    return new CfgValue(shape.Kind, bits, null, []);
                }
            }
            document.Attach(row);
            if (kind == BinaryFormat.T2b) document.MutableRows.Add(row);
            else (list ?? throw new InvalidDataException("RDBNP content must follow a LIST declaration.")).MutableRows.Add(row);
            rowLayout = null;
        }
        if (rowLayout is not null) throw new InvalidDataException("Final ROW metadata has no content.");
        if (kind == BinaryFormat.Rdbnp && (document.Rdb is null || document.Lists.Count != document.Rdb.Roots.Count)) throw new InvalidDataException("RDBNP root lists are incomplete.");
        return document;
    }
    private static bool IsIdentifier(string text) => text.Length > 0 && !text.StartsWith("//", StringComparison.Ordinal) && !text.StartsWith('@') && text.All(c => !char.IsWhiteSpace(c) && c is not ',' and not ';' and not '"' and not '\\');
    private static CfgValue ParsePlain(Token token)
    {
        if (token.Quoted) return CfgValue.String(token.Text);
        if (token.Text == "null") return CfgValue.String(null);
        if (!token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && (token.Text.Contains('.') || token.Text.Contains('e') || token.Text.Contains('E') || token.Text is "NaN" or "Infinity" or "-Infinity")) return CfgValue.Float(float.Parse(token.Text, NumberStyles.Float, Invariant));
        return CfgValue.Signed(checked((int)CfgSyntax.Integer(token)));
    }
    private static ulong ParseUnsigned(Token token) => token.Quoted ? throw new InvalidDataException("Expected unsigned number.") : token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(token.Text.AsSpan(2), NumberStyles.HexNumber, Invariant) : ulong.Parse(token.Text, NumberStyles.Integer, Invariant);
    private static IEnumerable<CfgValue> Flatten(CfgValue value) => value.Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector ? value.Items.SelectMany(Flatten) : [value];
    private static string Print(CfgValue value) => value.Kind switch
    {
        ValueKind.String => value.Text is null ? "null" : CfgSyntax.Quote(value.Text),
        ValueKind.Signed => unchecked((long)value.Bits).ToString(Invariant),
        ValueKind.Float32 => float.IsNaN(value.AsSingle()) ? $"0x{value.Bits:X8}" : FloatText(value.AsSingle().ToString("R", Invariant)),
        ValueKind.Float64 => double.IsNaN(value.AsDouble()) ? $"0x{value.Bits:X16}" : FloatText(value.AsDouble().ToString("R", Invariant)),
        ValueKind.Hash32 or ValueKind.Hash64 or ValueKind.Opaque => $"0x{value.Bits:X}",
        ValueKind.Reference16 or ValueKind.Reference32 => value.AsRange().Start.ToString(Invariant) + ", " + value.AsRange().Count.ToString(Invariant),
        _ => value.Bits.ToString(Invariant)
    };
    private static string FloatText(string text) => text.Contains('.') || text.Contains('E') || text.Contains("Infinity", StringComparison.Ordinal) ? text : text + ".0";
    private static ValueShape Shape(CfgValue value) => new(value.Kind, value.Kind == ValueKind.String ? value.Bits : 0, value.Items.Select(Shape).ToArray());
    private sealed record ValueShape(ValueKind Kind, ulong StringOffset, ValueShape[] Items);
    private sealed record RowLayout(uint Hash, string[] Fields, byte[] Padding, byte[] Types, int[] StringOffsets, List<OpaqueRegion> RdbPadding, ValueShape[] Values);
}
