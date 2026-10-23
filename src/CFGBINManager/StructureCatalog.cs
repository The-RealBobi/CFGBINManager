using System.Globalization;

namespace CFGBINManager;

public sealed record CountRule(string Start, int Position, string Child, string End);
public sealed record RowIndexRule(string Source, int Position, string Target, int? Absent);
public sealed record RangeRule(string Source, int StartPosition, int CountPosition, string Target);
public sealed record ReferenceRule(string Type, string Field, string Target);

public sealed class StructureCatalog
{
    private readonly Dictionary<(BinaryFormat, string), string> entries = [];
    private readonly Dictionary<(BinaryFormat, string, string), string> fields = [];
    private readonly Dictionary<(string, uint, string), int> layouts = [];
    private readonly Dictionary<string, string> indices = new(StringComparer.Ordinal);
    private readonly List<CountRule> counts = [];
    private readonly List<ReferenceRule> references = [];
    private readonly List<RangeRule> ranges = [];
    private readonly List<RowIndexRule> rowIndices = [];
    public IReadOnlyList<RowIndexRule> RowIndices => rowIndices.AsReadOnly();
    public IReadOnlyList<RangeRule> Ranges => ranges.AsReadOnly();
    public IReadOnlyList<CountRule> Counts => counts.AsReadOnly();
    public IReadOnlyList<ReferenceRule> References => references.AsReadOnly();
    private Dictionary<string, StructureCatalog>? profiles;
    private readonly Dictionary<(BinaryFormat, string), IReadOnlyList<StructureField>> definitions = [];
    private readonly HashSet<(BinaryFormat, string, string)> ambiguousFields = [];
    public string? ProfileName { get; private set; }
    public IReadOnlyList<string> ProfileNames => profiles?.Keys.ToArray() ?? [];
    public StructureCatalog ForProfile(string name) => profiles is not null && profiles.TryGetValue(name, out var profile) ? profile : throw new KeyNotFoundException($"Structure profile '{name}' does not exist.");
    public IReadOnlyList<StructureField> GetFields(BinaryFormat format, string entry)
    {
        RequireProfileSelection();
        return definitions.GetValueOrDefault((format, ResolveEntry(format, entry)), []);
    }
    public IReadOnlyList<StructureField> GetFields(BinaryFormat format, string entry, int valueCount)
    {
        if (valueCount < 0) throw new ArgumentOutOfRangeException(nameof(valueCount));
        var declared = GetFields(format, entry);
        return format == BinaryFormat.T2b && declared.Count != valueCount ? [] : declared;
    }
    internal void RequireProfileSelection()
    {
        if (profiles is not null && profiles.Count > 1 && ProfileName is null) throw new InvalidOperationException("Select a game with ForProfile before using this catalog.");
    }
    private StructureCatalog() { }
    public static StructureCatalog Parse(string text)
    {
        string first = text.TrimStart('\uFEFF').Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)) ?? "";
        if (first.StartsWith("STRUCTURE_VERSION", StringComparison.Ordinal)) return ParseDeclarations(text);
        var profiles = new Dictionary<string, StructureCatalog>(StringComparer.Ordinal);
        foreach (StructureProfile profile in StructureConfig.Read(text))
        {
            var selected = ParseDeclarations(StructureConfig.Declarations(profile));
            selected.ProfileName = profile.Name;
            foreach (StructureEntry entry in profile.Entries)
            {
                selected.definitions.Add((entry.Format, entry.Name), Array.AsReadOnly(entry.Fields.ToArray()));
                foreach (var name in entry.Fields.SelectMany(field => field.Alias is null || field.Alias == field.Name ? new[] { field.Name } : new[] { field.Name, field.Alias }).GroupBy(name => name).Where(group => group.Count() > 1))
                    if (entry.Format != BinaryFormat.T2b || !int.TryParse(name.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) selected.ambiguousFields.Add((entry.Format, entry.Name, name.Key));
            }
            profiles.Add(profile.Name, selected);
        }
        foreach (var selected in profiles.Values) selected.profiles = profiles;
        return profiles.Count == 1 ? profiles.Values.Single() : new StructureCatalog { profiles = profiles };
    }
    private static StructureCatalog ParseDeclarations(string text)
    {
        var catalog = new StructureCatalog();
        bool version = false;
        foreach (var statement in CfgSyntax.Parse(text))
        {
            var v = statement.Values;
            string S(int i) => v[i].Text;
            int N(int i) => checked((int)CfgSyntax.Integer(v[i]));
            BinaryFormat F() => S(0) switch { "T2B" => BinaryFormat.T2b, "RDBNP" => BinaryFormat.Rdbnp, _ => throw new InvalidDataException($"Unknown catalog format '{S(0)}'.") };
            int required = statement.Name switch { "STRUCTURE_VERSION" => 1, "ENTRY" => 3, "FIELD" => 4, "COUNT" => 5, "RANGE" => 5, "ROW_INDEX" => 5, "REFERENCE" => 3, "INDEX_KEY" => 2, "LAYOUT_FIELD" => 4, _ => throw new InvalidDataException($"Unknown catalog declaration '{statement.Name}'.") };
            if (v.Count != required) throw new InvalidDataException($"{statement.Name} requires {required} arguments.");
            switch (statement.Name)
            {
                case "STRUCTURE_VERSION": if (N(0) != 1 || version) throw new InvalidDataException("Expected exactly one STRUCTURE_VERSION 1 declaration."); version = true; break;
                case "ENTRY": Add(catalog.entries, (F(), S(2)), S(1)); break;
                case "FIELD": Add(catalog.fields, (F(), S(1), S(3)), S(2)); break;
                case "COUNT":
                    if (F() != BinaryFormat.T2b || N(2) < 0) throw new InvalidDataException("COUNT requires T2B and a nonnegative value position.");
                    var count = new CountRule(S(1), N(2), S(3), S(4));
                    if (!catalog.counts.Contains(count)) catalog.counts.Add(count); break;
                case "RANGE":
                    if (F() != BinaryFormat.T2b || N(2) < 0 || N(3) < 0 || N(2) == N(3)) throw new InvalidDataException("RANGE requires T2B and distinct nonnegative positions.");
                    var range = new RangeRule(S(1), N(2), N(3), S(4));
                    if (!catalog.ranges.Contains(range)) catalog.ranges.Add(range); break;
                case "ROW_INDEX":
                    if (F() != BinaryFormat.T2b || N(2) < 0) throw new InvalidDataException("ROW_INDEX requires T2B and a nonnegative position.");
                    var rowIndex = new RowIndexRule(S(1), N(2), S(3), !v[4].Quoted && S(4) == "null" ? null : N(4));
                    if (!catalog.rowIndices.Contains(rowIndex)) catalog.rowIndices.Add(rowIndex); break;
                case "REFERENCE":
                    var reference = new ReferenceRule(S(0), S(1), S(2));
                    if (catalog.references.Any(r => r.Type == reference.Type && r.Field == reference.Field && r.Target != reference.Target)) throw new InvalidDataException($"Conflicting reference for {reference.Type}.{reference.Field}.");
                    if (!catalog.references.Contains(reference)) catalog.references.Add(reference); break;
                case "INDEX_KEY": Add(catalog.indices, S(0), S(1)); break;
                case "LAYOUT_FIELD": if (N(3) < 0) throw new InvalidDataException("Layout offsets cannot be negative."); Add(catalog.layouts, (S(0), checked((uint)CfgSyntax.Integer(v[1])), S(2)), N(3)); break;
            }
        }
        if (!version) throw new InvalidDataException("Missing STRUCTURE_VERSION 1 declaration.");
        foreach (var alias in catalog.entries)
            if (alias.Key.Item2 != alias.Value && catalog.entries.Any(other => other.Key.Item1 == alias.Key.Item1 && other.Value == alias.Key.Item2))
                throw new InvalidDataException($"Entry alias '{alias.Key.Item2}' shadows a native name.");
        foreach (var alias in catalog.fields)
            if (alias.Key.Item3 != alias.Value && (catalog.fields.Any(other => other.Key.Item1 == alias.Key.Item1 && other.Key.Item2 == alias.Key.Item2 && other.Value == alias.Key.Item3) || alias.Key.Item1 == BinaryFormat.T2b && int.TryParse(alias.Key.Item3, out _)))
                throw new InvalidDataException($"Field alias '{alias.Key.Item3}' shadows a native field.");
        if (catalog.counts.GroupBy(rule => (rule.Start, rule.Position)).Any(group => group.Count() != 1)) throw new InvalidDataException("A counter has conflicting COUNT declarations.");
        if (catalog.ranges.SelectMany(rule => new[] { (rule.Source, rule.StartPosition), (rule.Source, rule.CountPosition) }).GroupBy(field => field).Any(group => group.Count() != 1)) throw new InvalidDataException("A reference has conflicting RANGE declarations.");
        if (catalog.rowIndices.GroupBy(rule => (rule.Source, rule.Position)).Any(group => group.Count() != 1) || catalog.rowIndices.Any(rule => catalog.ranges.Any(range => range.Source == rule.Source && (range.StartPosition == rule.Position || range.CountPosition == rule.Position)))) throw new InvalidDataException("A field has conflicting row index declarations.");
        return catalog;
    }
    private static void Add<K,V>(Dictionary<K,V> dictionary, K key, V value) where K : notnull
    {
        if (dictionary.TryGetValue(key, out var old) && !EqualityComparer<V>.Default.Equals(old, value)) throw new InvalidDataException($"Conflicting catalog alias '{key}'.");
        dictionary[key] = value;
    }
    public string ResolveEntry(BinaryFormat format, string name)
    {
        RequireProfileSelection();
        return entries.GetValueOrDefault((format, name), name);
    }
    public string ResolveField(BinaryFormat format, string type, string field)
    {
        RequireProfileSelection();
        if (ambiguousFields.Contains((format, type, field))) throw new InvalidOperationException($"Field label '{field}' appears more than once in {type}; use its numeric position.");
        return fields.GetValueOrDefault((format, type, field), field);
    }
    public string ResolveField(BinaryFormat format, string type, string field, int valueCount)
    {
        if (valueCount < 0) throw new ArgumentOutOfRangeException(nameof(valueCount));
        RequireProfileSelection();
        if (format == BinaryFormat.T2b && definitions.TryGetValue((format, type), out var declared) && declared.Count != valueCount)
            return field;
        return ResolveField(format, type, field);
    }
    public int? GetLayoutOffset(string type, uint layoutId, string field) => layouts.TryGetValue((type, layoutId, field), out int offset) ? offset : null;
    public string? GetIndexKey(string list) => indices.GetValueOrDefault(list);
    internal void RebaseRanges(CfgDocument document, string target, int rowIndex, bool insertion)
    {
        ValidateLinks(document);
        var indexEdits = new List<(CfgRow Row, int Position, int Value)>();
        int position = document.Rows.Take(rowIndex).Count(row => row.Name == target);
        foreach (RowIndexRule rule in rowIndices.Where(rule => rule.Target == target))
            foreach (CfgRow row in document.Rows.Where(row => row.Name == rule.Source))
            {
                int value = checked((int)row.Values[rule.Position].AsInt64());
                if (value == rule.Absent) continue;
                if (!insertion && value == position) throw new InvalidDataException($"Cannot remove {target}[{position}]: {rule.Source}[{rule.Position}] still references it.");
                if (insertion ? value >= position : value > position) value = checked(value + (insertion ? 1 : -1));
                if (value == rule.Absent) throw new InvalidDataException($"Rebased {rule.Source} index would collide with its absent sentinel {rule.Absent}.");
                indexEdits.Add((row, rule.Position, value));
            }
        int size = document.Rows.Count(row => row.Name == target);
        var edits = new List<(CfgRow Row, RangeRule Rule, int Start, int Count)>();
        foreach (RangeRule rule in ranges.Where(rule => rule.Target == target))
        {
            foreach (CfgRow row in document.Rows.Where(row => row.Name == rule.Source))
            {
                if ((uint)rule.StartPosition >= row.Values.Count || (uint)rule.CountPosition >= row.Values.Count) throw new InvalidDataException($"{rule.Source} range fields are missing.");
                int start = checked((int)row.Values[rule.StartPosition].AsInt64()), count = checked((int)row.Values[rule.CountPosition].AsInt64());
                if (start < 0 || count < 0 || start > size - count) throw new InvalidDataException($"{rule.Source}: range {start}+{count} exceeds {target} ({size}).");
                if (position < start) start = checked(start + (insertion ? 1 : -1));
                else if (position < start + count) count = checked(count + (insertion ? 1 : -1));
                edits.Add((row, rule, start, count));
            }
        }
        foreach (var edit in indexEdits) edit.Row.Assign(edit.Position, CfgValue.Signed(edit.Value));
        foreach (var edit in edits)
        {
            edit.Row.Assign(edit.Rule.StartPosition, CfgValue.Signed(edit.Start));
            edit.Row.Assign(edit.Rule.CountPosition, CfgValue.Signed(edit.Count));
        }
    }
    internal void ValidateLinks(CfgDocument document)
    {
        foreach (RangeRule rule in ranges)
        {
            int size = document.Rows.Count(row => row.Name == rule.Target);
            foreach (CfgRow row in document.Rows.Where(row => row.Name == rule.Source))
            {
                if ((uint)rule.StartPosition >= row.Values.Count || (uint)rule.CountPosition >= row.Values.Count) throw new InvalidDataException($"{rule.Source} range fields are missing.");
                long start = row.Values[rule.StartPosition].AsInt64(), count = row.Values[rule.CountPosition].AsInt64();
                if (start < 0 || count < 0 || start > size - count) throw new InvalidDataException($"{rule.Source}: range {start}+{count} exceeds {rule.Target} ({size}).");
            }
        }
        foreach (RowIndexRule rule in rowIndices)
        {
            int size = document.Rows.Count(row => row.Name == rule.Target);
            foreach (CfgRow row in document.Rows.Where(row => row.Name == rule.Source))
            {
                if ((uint)rule.Position >= row.Values.Count) throw new InvalidDataException($"{rule.Source} index field is missing.");
                long index = row.Values[rule.Position].AsInt64();
                if (index != rule.Absent && (index < 0 || index >= size)) throw new InvalidDataException($"{rule.Source}: index {index} exceeds {rule.Target} ({size}).");
            }
        }
    }
    internal IReadOnlyDictionary<(CfgRow Row, int Position), CfgValue> CalculateCounts(CfgDocument document)
    {
        var updates = new Dictionary<(CfgRow Row, int Position), CfgValue>();
        void Finish(CountRule rule, (CfgRow Row, int Count) scope)
        {
            if ((uint)rule.Position >= scope.Row.Values.Count || scope.Row.Values[rule.Position].Kind != ValueKind.Signed) throw new InvalidDataException($"Counter {rule.Start}[{rule.Position}] is not an Int32 value.");
            updates.Add((scope.Row, rule.Position), CfgValue.Signed(scope.Count));
        }
        foreach (CountRule rule in counts)
        {
            var stack = new Stack<(CfgRow Row, int Count)>();
            foreach (CfgRow row in document.Rows)
            {
                if (row.Name == rule.Start) stack.Push((row, 0));
                else if (rule.End.Length != 0 && row.Name == rule.End)
                {
                    if (stack.Count == 0) throw new InvalidDataException($"Counted group {rule.End} has no opening {rule.Start}.");
                    Finish(rule, stack.Pop());
                }
                else if (row.Name == rule.Child && stack.Count > 0)
                { var scope = stack.Pop(); stack.Push((scope.Row, checked(scope.Count + 1))); }
            }
            if (rule.End.Length == 0)
            {
                if (stack.Count > 1) throw new InvalidDataException($"End-of-file counted group {rule.Start} has multiple openings.");
                if (stack.Count == 1) Finish(rule, stack.Pop());
            }
            else if (stack.Count != 0) throw new InvalidDataException($"Counted group {rule.Start} has no closing {rule.End}.");
        }
        return updates;
    }
}
