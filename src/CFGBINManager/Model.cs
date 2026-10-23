using System.Globalization;

namespace CFGBINManager;

public enum BinaryFormat { T2b, Rdbnp }
public sealed record CfgField(string Name, ushort TypeCode, ushort Flags, int ElementSize, int Count, int StoredOffset, int Offset)
{
    public int? PrimitiveSize => (Flags & 1) != 0 ? RdbnpCodec.PrimitiveSize(TypeCode) : null;
}
public sealed record CfgType(string Name, uint NameHash, uint LayoutId, IReadOnlyList<CfgField> Fields);
public sealed record T2bDocumentInfo(int StringOffset, int StringLength, int StringCount, int EncodingCode);
public sealed record T2bRowInfo(int ValueOffset, IReadOnlyList<uint> RawValues, IReadOnlyList<string> NameCandidates);
public sealed record CfgStringEdit(int RowIndex, int ValueIndex, string Text);

public enum ValueKind { String, Signed, Unsigned, Float32, Float64, Hash32, Hash64, Boolean, Vector, Reference16, Reference32, Array, Structure, Opaque }

public sealed record CfgValue(ValueKind Kind, ulong Bits, string? Text, IReadOnlyList<CfgValue> Items)
{
    public static CfgValue Signed(long value) => new(ValueKind.Signed, unchecked((ulong)value), null, []);
    public static CfgValue Unsigned(ulong value) => new(ValueKind.Unsigned, value, null, []);
    public static CfgValue String(string? value) => new(ValueKind.String, 0, value, []);
    public static CfgValue Boolean(bool value) => new(ValueKind.Boolean, value ? 1u : 0u, null, []);
    public static CfgValue Float(float value) => new(ValueKind.Float32, BitConverter.SingleToUInt32Bits(value), null, []);
    public static CfgValue Double(double value) => new(ValueKind.Float64, BitConverter.DoubleToUInt64Bits(value), null, []);
    public static CfgValue Range16(ushort start, ushort count) => new(ValueKind.Reference16, (uint)start | ((uint)count << 16), null, []);
    public static CfgValue Range32(uint start, uint count) => new(ValueKind.Reference32, start | ((ulong)count << 32), null, []);
    public long AsInt64() => Kind == ValueKind.Signed ? unchecked((long)Bits) : checked((long)AsUInt64());
    public ulong AsUInt64() => Kind is ValueKind.Unsigned or ValueKind.Hash32 or ValueKind.Hash64 or ValueKind.Boolean ? Bits : throw new InvalidOperationException($"{Kind} is not unsigned.");
    public bool AsBoolean() => Kind == ValueKind.Boolean ? Bits != 0 : throw new InvalidOperationException($"{Kind} is not Boolean.");
    public string? AsString() => Kind == ValueKind.String ? Text : throw new InvalidOperationException($"{Kind} is not a string.");
    public float AsSingle() => Kind == ValueKind.Float32 ? BitConverter.UInt32BitsToSingle(checked((uint)Bits)) : throw new InvalidOperationException($"{Kind} is not Float32.");
    public double AsDouble() => Kind == ValueKind.Float64 ? BitConverter.UInt64BitsToDouble(Bits) : AsSingle();
    public (uint Start, uint Count) AsRange() => Kind switch
    {
        ValueKind.Reference16 => ((uint)(Bits & 65535), (uint)(Bits >> 16)),
        ValueKind.Reference32 => ((uint)Bits, (uint)(Bits >> 32)),
        _ => throw new InvalidOperationException($"{Kind} is not a reference range.")
    };
}

public sealed class CfgRow
{
    private readonly List<CfgValue> values;
    private StructureCatalog? lookupCatalog;
    private BinaryFormat lookupFormat;
    internal CfgDocument? Document { get; set; }
    public string Name { get; }
    public uint NameHash { get; }
    public IReadOnlyList<CfgValue> Values => values.AsReadOnly();
    public IReadOnlyList<string> FieldNames { get; }
    public T2bRowInfo? T2bInfo { get; internal set; }
    internal List<OpaqueRegion> RdbPadding { get; set; } = [];
    internal byte[] Padding { get; set; }
    internal int[] StringOffsets { get; set; }
    internal byte[] TypeBytes { get; set; }
    public CfgRow(string name, uint nameHash, IReadOnlyList<string> fieldNames, IEnumerable<CfgValue> values)
    {
        Name = name; NameHash = nameHash; FieldNames = fieldNames.ToArray(); this.values = values.ToList();
        Padding = []; TypeBytes = []; StringOffsets = Enumerable.Repeat(-1, this.values.Count).ToArray();
    }
    public CfgRow Clone() => new(Name, NameHash, FieldNames, values)
    {
        lookupCatalog = Document?.Catalog ?? lookupCatalog, lookupFormat = Document?.Format ?? lookupFormat,
        Padding = Padding.ToArray(), TypeBytes = TypeBytes.ToArray(), StringOffsets = StringOffsets.ToArray(),
        RdbPadding = RdbPadding.Select(region => new OpaqueRegion(region.Offset, region.Bytes.ToArray())).ToList()
    };
    public CfgValue GetValue(string name) => values[FieldIndex(name)];
    public void SetValue(string name, CfgValue value) => SetValue(FieldIndex(name), value);
    public void SetValue(int index, CfgValue value)
    {
        if (values[index].Kind != value.Kind) throw new ArgumentException($"Field {Name}[{index}] requires {values[index].Kind}.");
        values[index] = value;
    }
    internal void Assign(int index, CfgValue value) => values[index] = value;
    private int FieldIndex(string name)
    {
        string native = (Document?.Catalog ?? lookupCatalog)?.ResolveField(Document?.Format ?? lookupFormat, Name, name, values.Count) ?? name;
        if (int.TryParse(native, NumberStyles.None, CultureInfo.InvariantCulture, out int position) && (uint)position < values.Count) return position;
        if (native != name && FieldNames.Contains(name, StringComparer.Ordinal)) throw new InvalidOperationException($"Field alias '{name}' conflicts with a native field in {Name}.");
        var matches = FieldNames.Select((field, index) => (field, index)).Where(x => x.field == native).ToArray();
        return matches.Length == 1 ? matches[0].index : throw new KeyNotFoundException($"Field '{name}' is missing or ambiguous in {Name}.");
    }
}

public sealed class CfgList
{
    internal readonly List<CfgRow> MutableRows = [];
    public string Name { get; internal set; } = "";
    public string TypeName { get; internal set; } = "";
    public IReadOnlyList<CfgRow> Rows => MutableRows.AsReadOnly();
    internal RdbRoot? Layout { get; set; }
}

public sealed class CfgDocument
{
    internal readonly List<CfgRow> MutableRows = [];
    internal readonly List<CfgList> MutableLists = [];
    public BinaryFormat Format { get; }
    public StructureCatalog Catalog { get; }
    public T2bDocumentInfo? T2bInfo { get; internal set; }
    public ushort? BinaryVersion => Rdb?.Version;
    public bool IsBigEndian => Rdb?.BigEndian ?? false;
    public IReadOnlyList<CfgType> Types => Rdb is null ? [] : Rdb.Types.Select((type, index) => new CfgType(type.Name, type.Hash, type.LayoutId,
        RdbnpCodec.TypeFields(Rdb, index, Catalog).Select(item => new CfgField(item.Field.Name, item.Field.Code, item.Field.Flags, item.Field.Size, item.Field.Count, item.Field.Offset, item.Offset)).ToArray())).ToArray();
    public IReadOnlyList<CfgRow> Rows => MutableRows.AsReadOnly();
    public IReadOnlyList<CfgList> Lists => MutableLists.AsReadOnly();
    internal T2bLayout? T2b { get; set; }
    internal RdbLayout? Rdb { get; set; }
    internal CfgDocument(BinaryFormat format, StructureCatalog catalog) { Format = format; Catalog = catalog; }
    public IReadOnlyList<CfgRow> FindRows(string name)
    {
        string native = Catalog.ResolveEntry(Format, name);
        IEnumerable<CfgRow> rows = Format == BinaryFormat.T2b ? MutableRows : MutableLists.SelectMany(x => x.Rows);
        if (native != name && rows.Any(row => row.Name == name)) throw new InvalidOperationException($"Entry alias '{name}' conflicts with a native name in this document.");
        return rows.Where(row => row.Name == native).ToArray();
    }
    public CfgList GetList(string name) => MutableLists.SingleOrDefault(x => x.Name == name) ?? throw new KeyNotFoundException($"List '{name}' does not exist.");
    public IReadOnlyList<CfgField> GetListFields(string name)
    {
        if (Rdb is null) throw new InvalidOperationException("Native list descriptors require RDBNP.");
        var root = GetList(name).Layout ?? throw new InvalidOperationException("The list has no native root descriptor.");
        return RdbnpCodec.RootFields(Rdb, root, Catalog).Select(item => new CfgField(item.Field.Name, item.Field.Code,
            item.Field.Flags, item.Field.Size, item.Field.Count, item.Field.Offset, item.Offset)).ToArray();
    }
    public void InsertRow(int index, CfgRow row)
    {
        if (Format != BinaryFormat.T2b) throw new InvalidOperationException("Use InsertListRow for RDBNP.");
        if ((uint)index > MutableRows.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (row.Document is not null) throw new ArgumentException("Insert a new row, rather than a row already attached to a document.");
        Catalog.RebaseRanges(this, row.Name, index, true);
        Attach(row); MutableRows.Insert(index, row);
    }
    public void RemoveRow(int index)
    {
        if (Format != BinaryFormat.T2b) throw new InvalidOperationException("Use RemoveListRow for RDBNP.");
        Catalog.RebaseRanges(this, MutableRows[index].Name, index, false);
        MutableRows[index].Document = null;
        MutableRows.RemoveAt(index);
    }
    internal void Attach(CfgRow row)
    {
        if (row.Document is not null && row.Document != this) throw new ArgumentException("A row already belongs to another document. Create a new row to insert it here.");
        row.Document = this;
    }
    public void InsertListRow(string list, int index, CfgRow row) => RdbnpCodec.InsertRow(this, GetList(list), index, row);
    public void RemoveListRow(string list, int index) => RdbnpCodec.RemoveRow(this, GetList(list), index);
}

// Only physical layout and opaque padding are retained here; row payloads are always serialized from values.
internal sealed class T2bLayout
{
    public int EncodingCode { get; set; } = 1;
    public List<PoolString> Strings { get; set; } = [];
    public List<PoolString> Names { get; set; } = [];
    public List<NameRecord> NameRecords { get; set; } = [];
    public byte[] EntryGap { get; set; } = [];
    public byte[] PoolGap { get; set; } = [];
    public byte[] NameGap { get; set; } = [];
    public byte[] NameTail { get; set; } = [];
    public byte[] Footer { get; set; } = [1, 116, 50, 98, 254, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
}
internal sealed record PoolString(int Offset, string Text, byte[]? Encoded);
internal sealed record NameRecord(uint Hash, int Offset);
internal sealed record OpaqueRegion(int Offset, byte[] Bytes);
internal sealed class RdbField
{
    public uint Hash { get; set; }
    public string Name { get; set; } = "";
    public ushort Code { get; set; }
    public ushort Flags { get; set; }
    public int Size { get; set; }
    public int Offset { get; set; }
    public int Count { get; set; }
    public byte[] Tail { get; set; } = [];
}
internal sealed class RdbType
{
    public uint Hash { get; set; }
    public string Name { get; set; } = "";
    public uint LayoutId { get; set; }
    public int FieldIndex { get; set; }
    public int FieldCount { get; set; }
    public byte[] Tail { get; set; } = [];
}
internal sealed class RdbRoot
{
    public ushort Type { get; set; }
    public ushort Flags { get; set; }
    public int Offset { get; set; }
    public int RowSize { get; set; }
    public int RowCount { get; set; }
    public uint Hash { get; set; }
    public int IndexOffset { get; set; }
    public uint IndexKind { get; set; }
    public uint Tail { get; set; }
    public List<uint> Index { get; set; } = [];
    public string? IndexKey { get; set; }
    public string OriginalRowsFingerprint { get; set; } = "";
    public int OriginalRowCount { get; set; }
    public List<ulong> OriginalIndexKeys { get; set; } = [];
}
internal sealed class RdbLayout
{
    public string SchemaFingerprint { get; set; } = "";
    public ushort Version { get; set; }
    public bool BigEndian { get; set; }
    public int DataBase { get; set; }
    public int DataSize { get; set; }
    public int TypeOffset { get; set; }
    public int FieldOffset { get; set; }
    public int RootOffset { get; set; }
    public int HashOffset { get; set; }
    public int NameOffset { get; set; }
    public int ValueOffset { get; set; }
    public int StringOffset { get; set; }
    public int Length { get; set; }
    public byte[] HeaderExtra { get; set; } = [];
    public List<RdbType> Types { get; set; } = [];
    public List<RdbField> Fields { get; set; } = [];
    public List<RdbRoot> Roots { get; set; } = [];
    public List<NameRecord> Names { get; set; } = [];
    public List<PoolString> Strings { get; set; } = [];
    public List<OpaqueRegion> Gaps { get; set; } = [];
}
