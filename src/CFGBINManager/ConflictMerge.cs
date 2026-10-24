using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CFGBINManager;

public enum MergeSide { Current, Incoming }
public sealed record MergeKey(string Scope, string Entry, int Position);
public sealed record MergeKeyOptions(string Scope, string Entry, IReadOnlyList<int> Positions, int? SuggestedPosition);
public sealed record MergeConflict(int Id, string Location, string Field, CfgValue Current, CfgValue Incoming, CfgValue? CurrentTarget, CfgValue? IncomingTarget);
public enum MergeEntryState { Matched, File1Only, File2Only, Conflict }
public sealed record MergeField(string Name, CfgValue? File1, CfgValue? File2, int? ConflictId, CfgValue? AutomaticValue);
public sealed record MergeEntry(string Name, string Label, string Location, bool IsClosing, MergeEntryState State,
    IReadOnlyList<MergeField> Fields, IReadOnlyList<MergeEntry> Children);

/// <summary>A merge snapshot. Source documents are copied; constructing a result never changes either input.</summary>
public sealed class CfgMergePlan
{
    private sealed class Node
    {
        internal CfgRow? Current;
        internal CfgRow? Incoming;
        internal string Scope = "";
        internal string Location = "";
        internal string Label = "";
        internal readonly List<Node> Children = [];
        internal readonly List<ValueChoice> Values = [];
        internal readonly List<string> FieldNames = [];
    }

    private sealed record ValueChoice(CfgValue Current, CfgValue? Incoming, int? Conflict, IReadOnlyList<ValueChoice> Children);
    private readonly CfgDocument current;
    private readonly CfgDocument incoming;
    private readonly Dictionary<(string Scope, string Entry), int> keys;
    private readonly Dictionary<CfgRow, Node> currentNodes = [];
    private readonly Dictionary<CfgRow, Node> incomingNodes = [];
    private readonly Dictionary<CfgRecordNode, string> fingerprints = [];
    private readonly List<MergeConflict> conflicts = [];
    private readonly List<Node> roots;
    private readonly Dictionary<string, Dictionary<Node, int>> positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node[]> targetNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(CfgDocument Document, string Target), CfgRow[]> targets = [];
    public IReadOnlyList<MergeConflict> Conflicts => conflicts.AsReadOnly();
    public IReadOnlyList<MergeEntry> Entries { get; }
    public int AddedRows { get; private set; }
    public int MatchedRows { get; private set; }
    public int CurrentOnlyRows { get; private set; }

    public static IReadOnlyList<MergeKeyOptions> GetKeyOptions(CfgDocument current, CfgDocument incoming)
    {
        CheckCompatibility(current, incoming);
        var left = SiblingGroups(current); var right = SiblingGroups(incoming);
        var result = new List<MergeKeyOptions>();
        foreach (var key in left.Keys.Union(right.Keys).OrderBy(key => key.Scope, StringComparer.Ordinal).ThenBy(key => key.Entry, StringComparer.Ordinal))
        {
            CfgRow[][] leftGroups = left.GetValueOrDefault(key, []), rightGroups = right.GetValueOrDefault(key, []);
            var groups = leftGroups.Concat(rightGroups).ToArray();
            if (groups.All(group => group.Length <= 1)) continue;
            CfgRow[] a = leftGroups.SelectMany(group => group).ToArray(), b = rightGroups.SelectMany(group => group).ToArray();
            var rows = a.Concat(b).ToArray();
            int width = rows.Min(row => row.Values.Count);
            var candidates = Enumerable.Range(0, width).Where(i => rows.All(row => row.Values[i].Items.Count == 0
                && row.Values[i].Kind is not (ValueKind.Reference16 or ValueKind.Reference32))
                && groups.All(group => group.Select(row => Token(row.Values[i])).Distinct(StringComparer.Ordinal).Count() == group.Length)
                && !IsMaintained(current, key.Entry, i)).ToArray();
            int? suggested = candidates.Cast<int?>().FirstOrDefault(i => a.Select(row => Token(row.Values[i!.Value])).Intersect(b.Select(row => Token(row.Values[i!.Value])), StringComparer.Ordinal).Any());
            result.Add(new(key.Scope, key.Entry, candidates, suggested));
        }
        return result.AsReadOnly();
    }

    public static CfgMergePlan Analyze(CfgDocument current, CfgDocument incoming, IReadOnlyList<MergeKey> keys)
        => new(current, incoming, keys);

    private CfgMergePlan(CfgDocument source, CfgDocument mod, IReadOnlyList<MergeKey> selectedKeys)
    {
        CheckCompatibility(source, mod);
        var snapshot = CopyCatalog(source.Catalog);
        current = CfgTextCodec.Read(CfgTextCodec.Write(source), snapshot);
        incoming = CfgTextCodec.Read(CfgTextCodec.Write(mod), snapshot);
        keys = new Dictionary<(string, string), int>();
        var currentGroups = RowGroups(current); var incomingGroups = RowGroups(incoming);
        foreach (var key in selectedKeys)
        {
            if (key.Position < 0 || !keys.TryAdd((key.Scope, key.Entry), key.Position)) throw new ArgumentException("Merge keys must have one nonnegative field position per entry and scope.", nameof(selectedKeys));
            var rows = currentGroups.GetValueOrDefault((key.Scope, key.Entry), []).Concat(incomingGroups.GetValueOrDefault((key.Scope, key.Entry), [])).ToArray();
            if (rows.Length == 0 || rows.Any(row => key.Position >= row.Values.Count || row.Values[key.Position].Items.Count != 0
                || row.Values[key.Position].Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector or ValueKind.Reference16 or ValueKind.Reference32)
                || IsMaintained(current, key.Entry, key.Position))
                throw new ArgumentException($"{key.Scope}/{key.Entry}: field {key.Position} is not a usable merge key.", nameof(selectedKeys));
        }
        current.Catalog.ValidateLinks(current); incoming.Catalog.ValidateLinks(incoming);
        roots = Pair(current.GetRecordTree(), incoming.GetRecordTree(), "", "");
        foreach (Node node in AllNodes())
        {
            CfgRow? row = node.Current ?? node.Incoming;
            if (row is null) continue;
            string target = current.Format == BinaryFormat.T2b ? row.Name : node.Scope;
            if (!positions.TryGetValue(target, out var map)) positions[target] = map = [];
            map.Add(node, map.Count);
        }
        foreach (var target in positions) targetNodes[target.Key] = target.Value.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();
        foreach (Node node in AllNodes())
        {
            if (node.Current is null && node.Incoming is null) continue;
            CfgValue[]? a = node.Current is null ? null : Normalize(current, node.Current, node.Scope);
            CfgValue[]? b = node.Incoming is null ? null : Normalize(incoming, node.Incoming, node.Scope);
            if (a is not null && b is not null && a.Length != b.Length) throw new InvalidDataException($"{node.Location}: field counts differ. These entries do not have the same structure.");
            var row = node.Current ?? node.Incoming!;
            if (a is not null && b is not null && current.Format == BinaryFormat.T2b)
            {
                foreach (var rule in current.Catalog.RowIndices.Where(rule => rule.Source == row.Name))
                {
                    int left = checked((int)a[rule.Position].AsInt64()), right = checked((int)b[rule.Position].AsInt64());
                    if (left != rule.Absent && right != rule.Absent && SameLeafTargets(rule.Target, left, right, 1)) b[rule.Position] = a[rule.Position];
                }
                foreach (var rule in current.Catalog.Ranges.Where(rule => rule.Source == row.Name))
                {
                    var left = a[rule.StartPosition].AsRange(); var right = b[rule.StartPosition].AsRange();
                    if (left.Count == right.Count && SameLeafTargets(rule.Target, checked((int)left.Start), checked((int)right.Start), checked((int)left.Count)))
                        b[rule.StartPosition] = a[rule.StartPosition];
                }
            }
            for (int i = 0; i < (a ?? b!).Length; i++)
            {
                string field = current.Catalog.GetFieldLabel(current.Format, row.Name, row.Values.Count, i.ToString(CultureInfo.InvariantCulture))
                    ?? current.Catalog.GetFields(current.Format, row.Name).FirstOrDefault(field => field.Position == i)?.Name ?? row.FieldNames[i];
                string? referenceTarget = null; int? absent = null;
                if (current.Format == BinaryFormat.T2b && current.Catalog.RowIndices.FirstOrDefault(rule => rule.Source == row.Name && rule.Position == i) is { } indexRule)
                { field += " → " + indexRule.Target; referenceTarget = indexRule.Target; absent = indexRule.Absent; }
                if (current.Format == BinaryFormat.T2b && current.Catalog.Ranges.FirstOrDefault(rule => rule.Source == row.Name && rule.StartPosition == i) is { } rangeRule)
                { field += " + " + rangeRule.CountPosition + " → " + rangeRule.Target + " (range)"; referenceTarget = rangeRule.Target; }
                bool maintained = current.Format == BinaryFormat.T2b && current.Catalog.Counts.Any(rule => rule.Start == row.Name && rule.Position == i);
                node.FieldNames.Add(field);
                node.Values.Add(Compare(a?[i] ?? b![i], maintained ? null : a is null ? null : b?[i], node.Location, field, referenceTarget, absent));
            }
        }
        CurrentOnlyRows = currentNodes.Count - MatchedRows;
        var counts = new Dictionary<(CfgRow Row, int Position), CfgValue>();
        if (current.Format == BinaryFormat.T2b)
        {
            var preview = new CfgDocument(BinaryFormat.T2b, current.Catalog);
            preview.MutableRows.AddRange(AllNodes().Select(node => node.Current ?? node.Incoming).OfType<CfgRow>());
            foreach (var count in current.Catalog.CalculateCounts(preview)) counts.Add(count.Key, count.Value);
        }
        Entries = Array.AsReadOnly(roots.Select(node => Project(node, null)).ToArray());

        MergeEntry Project(Node node, string? parentEnd)
        {
            CfgRow? row = node.Current ?? node.Incoming;
            string name = row?.Name ?? node.Scope;
            string? end = row is not null && current.Format == BinaryFormat.T2b ? current.GetBlockEnd(name) : null;
            string label = node.Label;
            if (end is not null && end.EndsWith("_END", StringComparison.Ordinal))
            {
                string clean = end[..^4];
                if (new[] { "_BEG", "_BGN", "_BEGIN", "_START" }.Any(suffix => name == clean + suffix)) label = clean + label[name.Length..];
            }
            var fields = new List<MergeField>();
            for (int i = 0; i < node.Values.Count; i++)
            {
                if (row is not null && current.Format == BinaryFormat.T2b && current.Catalog.Ranges.Any(rule => rule.Source == row.Name && rule.CountPosition == i)) continue;
                if (row is not null && counts.TryGetValue((row, i), out var automatic))
                    fields.Add(new(node.FieldNames[i], node.Current?.Values[i], node.Incoming?.Values[i], null, automatic));
                else Add(node.Values[i], node.FieldNames[i]);
            }
            var state = row is null ? MergeEntryState.Matched : node.Current is null ? MergeEntryState.File2Only
                : node.Incoming is null ? MergeEntryState.File1Only : fields.Any(field => field.ConflictId is not null) ? MergeEntryState.Conflict : MergeEntryState.Matched;
            return new(name, label, node.Location, row is not null && parentEnd == name, state,
                fields.AsReadOnly(), Array.AsReadOnly(node.Children.Select(child => Project(child, end)).ToArray()));

            void Add(ValueChoice value, string field)
            {
                if (value.Children.Count > 0)
                    for (int i = 0; i < value.Children.Count; i++) Add(value.Children[i], field + "/" + i);
                else fields.Add(new(field, node.Current is null ? null : value.Current,
                    node.Incoming is null ? null : value.Incoming ?? value.Current, value.Conflict, null));
            }
        }
    }

    public CfgDocument Build(IReadOnlyDictionary<int, MergeSide> resolutions)
    {
        foreach (MergeConflict conflict in conflicts)
            if (!resolutions.TryGetValue(conflict.Id, out var side) || !Enum.IsDefined(side)) throw new ArgumentException($"Choose a value for conflict {conflict.Id + 1} before creating the result.", nameof(resolutions));
        var result = CfgTextCodec.Read(CfgTextCodec.Write(current), CopyCatalog(current.Catalog));
        if (result.Format == BinaryFormat.T2b) result.MutableRows.Clear();
        else foreach (CfgList list in result.Lists) list.MutableRows.Clear();
        foreach (Node node in AllNodes())
        {
            if (node.Current is null && node.Incoming is null) continue;
            CfgRow row = (node.Current ?? node.Incoming!).CloneForImport();
            for (int i = 0; i < node.Values.Count; i++) row.Assign(i, Resolve(node.Values[i], resolutions));
            if (result.Format == BinaryFormat.T2b)
                foreach (var range in current.Catalog.Ranges.Where(rule => rule.Source == row.Name))
                {
                    var value = row.Values[range.StartPosition].AsRange();
                    row.Assign(range.StartPosition, CfgValue.Signed(value.Start)); row.Assign(range.CountPosition, CfgValue.Signed(value.Count));
                }
            result.Attach(row);
            if (result.Format == BinaryFormat.T2b) result.MutableRows.Add(row);
            else result.GetList(node.Scope).MutableRows.Add(row);
        }
        _ = result.GetRecordTree();
        var validated = BinCodec.Read(BinCodec.Write(result), result.Catalog);
        _ = validated.GetRecordTree();
        return validated;
    }

    private List<Node> Pair(IReadOnlyList<CfgRecordNode> a, IReadOnlyList<CfgRecordNode> b, string scope, string parent)
    {
        var matches = new Dictionary<CfgRecordNode, CfgRecordNode>();
        string Name(CfgRecordNode node) => node.Row?.Name ?? "list:" + node.ListName;
        foreach (var group in b.GroupBy(Name))
        {
            var left = a.Where(node => Name(node) == group.Key).ToArray(); var right = group.ToArray();
            if (keys.TryGetValue((scope, group.Key), out int position))
            {
                string Key(CfgRecordNode node)
                {
                    if (node.Row is null || position >= node.Row.Values.Count || node.Row.Values[position].Items.Count != 0 || IsMaintained(current, node.Row.Name, position))
                        throw new InvalidDataException($"{parent}/{group.Key}: field {position} is not a usable matching key.");
                    return Token(node.Row.Values[position]);
                }
                var lookup = new Dictionary<string, CfgRecordNode>(StringComparer.Ordinal);
                foreach (var node in left) if (!lookup.TryAdd(Key(node), node)) throw new InvalidDataException($"{parent}/{group.Key}: File 1 has duplicate matching keys.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in right)
                {
                    string key = Key(node);
                    if (!seen.Add(key)) throw new InvalidDataException($"{parent}/{group.Key}: File 2 has duplicate matching keys.");
                    if (lookup.TryGetValue(key, out var match)) matches[node] = match;
                }
            }
            else if (left.Length == 1 && right.Length == 1) matches[right[0]] = left[0];
            else
            {
                var lookup = left.GroupBy(Fingerprint).ToDictionary(group => group.Key, group => new Queue<CfgRecordNode>(group), StringComparer.Ordinal);
                foreach (var node in right)
                    if (lookup.TryGetValue(Fingerprint(node), out var queue) && queue.TryDequeue(out var match)) matches[node] = match;
            }
        }
        var reverse = matches.ToDictionary(pair => pair.Value, pair => pair.Key);
        var result = new List<Node>();
        foreach (var node in a) result.Add(Create(node, reverse.GetValueOrDefault(node)));
        foreach (var node in b.Where(node => !matches.ContainsKey(node))) result.Add(Create(null, node));
        return result;

        Node Create(CfgRecordNode? left, CfgRecordNode? right)
        {
            var native = left ?? right!;
            string nextScope = native.Row is null ? native.ListName! : scope;
            string label = native.Row?.Name ?? native.ListName!;
            if (native.Row is { } row && keys.TryGetValue((scope, row.Name), out int key) && key < row.Values.Count)
                label += "[" + DisplayKey(row.Values[key]) + "]";
            var node = new Node { Current = left?.Row, Incoming = right?.Row, Scope = nextScope, Location = parent + "/" + label, Label = label };
            if (left?.Row is { } currentRow) currentNodes.Add(currentRow, node);
            if (right?.Row is { } incomingRow) incomingNodes.Add(incomingRow, node);
            if (node.Current is not null && node.Incoming is not null) MatchedRows++;
            else if (node.Incoming is not null) AddedRows++;
            string? end = current.Format == BinaryFormat.T2b && native.Row is not null ? current.GetBlockEnd(native.Row.Name) : null;
            // Pass the native closer separately from the human-readable location.
            node.Children.AddRange(PairChildren(left?.Children ?? [], right?.Children ?? [], nextScope, node.Location, end));
            return node;
        }
    }

    private List<Node> PairChildren(IReadOnlyList<CfgRecordNode> a, IReadOnlyList<CfgRecordNode> b, string scope, string location, string? end)
    {
        CfgRecordNode? leftEnd = a.LastOrDefault(node => node.Row?.Name == end);
        CfgRecordNode? rightEnd = b.LastOrDefault(node => node.Row?.Name == end);
        var result = Pair(a.Where(node => !ReferenceEquals(node, leftEnd)).ToArray(), b.Where(node => !ReferenceEquals(node, rightEnd)).ToArray(), scope, location);
        if (leftEnd is not null || rightEnd is not null)
            result.AddRange(Pair(leftEnd is null ? [] : [leftEnd], rightEnd is null ? [] : [rightEnd], scope, location));
        return result;
    }

    private CfgValue[] Normalize(CfgDocument source, CfgRow row, string scope)
    {
        var values = row.Values.ToArray();
        if (source.Format == BinaryFormat.T2b)
        {
            foreach (var rule in source.Catalog.RowIndices.Where(rule => rule.Source == row.Name))
            {
                int old = checked((int)values[rule.Position].AsInt64());
                if (old == rule.Absent) continue;
                int next = Map(source, rule.Target, old);
                if (next == rule.Absent) throw new InvalidDataException($"{row.Name}: merged index collides with its absent sentinel.");
                values[rule.Position] = CfgValue.Signed(next);
            }
            foreach (var rule in source.Catalog.Ranges.Where(rule => rule.Source == row.Name))
            {
                int start = checked((int)values[rule.StartPosition].AsInt64()), count = checked((int)values[rule.CountPosition].AsInt64());
                values[rule.StartPosition] = CfgValue.Range32(checked((uint)MapRange(source, rule.Target, start, count)), checked((uint)count));
                values[rule.CountPosition] = CfgValue.Signed(0);
            }
        }
        else
        {
            var fields = RdbnpCodec.RootFields(source.Rdb!, source.GetList(scope).Layout!, source.Catalog);
            for (int i = 0; i < values.Length; i++)
                values[i] = RdbnpCodec.TransformReferences(source, row.Name, fields[i].Field, values[i], (type, field, value) =>
                {
                    var range = value.AsRange();
                    if (range.Count == 0) return value;
                    var rule = source.Catalog.References.SingleOrDefault(rule => rule.Type == type && rule.Field == field)
                        ?? throw new InvalidDataException($"{type}.{field}: a REFERENCE declaration is required before merging live references.");
                    int start = MapRange(source, rule.Target, checked((int)range.Start), checked((int)range.Count));
                    return value.Kind == ValueKind.Reference16 ? CfgValue.Range16(checked((ushort)start), checked((ushort)range.Count)) : CfgValue.Range32(checked((uint)start), range.Count);
                });
        }
        return values;
    }

    private int Map(CfgDocument source, string target, int index)
    {
        if (!targets.TryGetValue((source, target), out var rows))
            targets[(source, target)] = rows = source.Format == BinaryFormat.T2b ? source.Rows.Where(row => row.Name == target).ToArray() : source.GetList(target).Rows.ToArray();
        if ((uint)index >= rows.Length) throw new InvalidDataException($"Reference to {target}[{index}] exceeds its source collection.");
        Node node = (ReferenceEquals(source, current) ? currentNodes : incomingNodes)[rows[index]];
        return positions[target][node];
    }

    private int MapRange(CfgDocument source, string target, int start, int count)
    {
        if (count == 0) return 0;
        int first = Map(source, target, start);
        for (int i = 1; i < count; i++)
            if (Map(source, target, checked(start + i)) != first + i) throw new InvalidDataException($"Merging would split or reorder a referenced range in {target}. Choose different matching keys.");
        return first;
    }

    private bool SameLeafTargets(string target, int a, int b, int count)
    {
        if (current.Catalog.RowIndices.Any(rule => rule.Source == target) || current.Catalog.Ranges.Any(rule => rule.Source == target)) return false;
        bool Stable(Node node) => node.Children.Count == 0 && (node.Current is null || node.Incoming is null
            || node.Current.Values.Count == node.Incoming.Values.Count && node.Current.Values.Zip(node.Incoming.Values).All(pair => Equal(pair.First, pair.Second)));
        for (int i = 0; i < count; i++)
        {
            Node left = targetNodes[target][a + i], right = targetNodes[target][b + i];
            if (!Stable(left) || !Stable(right)) return false;
            var leftRow = left.Current ?? left.Incoming!; var rightRow = right.Current ?? right.Incoming!;
            if (leftRow.Values.Count != rightRow.Values.Count || !leftRow.Values.Zip(rightRow.Values).All(pair => Equal(pair.First, pair.Second))) return false;
        }
        return true;
    }

    private ValueChoice Compare(CfgValue a, CfgValue? b, string location, string field, string? target, int? absent)
    {
        if (b is null || Equal(a, b)) return new(a, null, null, []);
        if (a.Kind != b.Kind || a.Items.Count != b.Items.Count) throw new InvalidDataException($"{location}.{field}: field structures differ.");
        if (a.Items.Count > 0)
            return new(a, b, null, a.Items.Select((item, i) => Compare(item, b.Items[i], location, field + "/" + i, null, null)).ToArray());
        int id = conflicts.Count; conflicts.Add(new(id, location, field, a, b, Preview(a, MergeSide.Current), Preview(b, MergeSide.Incoming)));
        return new(a, b, id, []);

        CfgValue? Preview(CfgValue value, MergeSide side)
        {
            if (target is null) return null;
            int start = value.Kind == ValueKind.Signed ? checked((int)value.AsInt64()) : checked((int)value.AsRange().Start);
            int count = value.Kind == ValueKind.Signed ? start == absent ? 0 : 1 : checked((int)value.AsRange().Count);
            var rows = new List<CfgValue>();
            for (int i = 0; i < Math.Min(count, 3); i++)
            {
                var node = targetNodes[target][start + i];
                var row = side == MergeSide.Current ? node.Current ?? node.Incoming! : node.Incoming ?? node.Current!;
                rows.Add(new CfgValue(ValueKind.Array, 0, null, row.Values.ToArray()));
            }
            return new CfgValue(ValueKind.Array, 0, null, rows);
        }
    }

    private static CfgValue Resolve(ValueChoice value, IReadOnlyDictionary<int, MergeSide> choices)
        => value.Conflict is int id ? choices[id] == MergeSide.Current ? value.Current : value.Incoming!
            : value.Children.Count == 0 ? value.Current : value.Current with { Items = value.Children.Select(child => Resolve(child, choices)).ToArray() };

    private IEnumerable<Node> AllNodes()
    {
        var pending = new Stack<Node>(roots.AsEnumerable().Reverse());
        while (pending.TryPop(out var node)) { yield return node; foreach (var child in node.Children.AsEnumerable().Reverse()) pending.Push(child); }
    }

    private string Fingerprint(CfgRecordNode node)
    {
        if (fingerprints.TryGetValue(node, out string? value)) return value;
        string payload = node.Row is null ? "list:" + node.ListName : node.Row.Name + ":" + node.Row.NameHash + ":" + string.Join(";", node.Row.Values.Select(Token));
        value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload + "[" + string.Join(",", node.Children.Select(Fingerprint)) + "]")));
        fingerprints[node] = value; return value;
    }

    private static string Token(CfgValue value)
    {
        var text = new StringBuilder();
        void Add(CfgValue item)
        {
            text.Append((int)item.Kind).Append(':');
            if (item.Kind == ValueKind.String) text.Append(item.Text?.Length ?? -1).Append(':').Append(item.Text);
            else text.Append(item.Bits.ToString(CultureInfo.InvariantCulture));
            text.Append('[').Append(item.Items.Count).Append(':'); foreach (var child in item.Items) Add(child); text.Append(']');
        }
        Add(value); return text.ToString();
    }

    private static bool Equal(CfgValue a, CfgValue b) => a.Kind == b.Kind && (a.Kind == ValueKind.String ? a.Text == b.Text : a.Bits == b.Bits)
        && a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(pair => Equal(pair.First, pair.Second));
    private static string DisplayKey(CfgValue value) => value.Kind == ValueKind.String ? value.Text ?? "null"
        : value.Kind == ValueKind.Signed ? value.AsInt64().ToString(CultureInfo.InvariantCulture) : value.Bits.ToString(CultureInfo.InvariantCulture);
    private static bool IsMaintained(CfgDocument document, string entry, int position) => document.Format == BinaryFormat.T2b
        && (document.Catalog.Counts.Any(rule => rule.Start == entry && rule.Position == position)
            || document.Catalog.RowIndices.Any(rule => rule.Source == entry && rule.Position == position)
            || document.Catalog.Ranges.Any(rule => rule.Source == entry && (rule.StartPosition == position || rule.CountPosition == position)));

    private static Dictionary<(string Scope, string Entry), CfgRow[]> RowGroups(CfgDocument document)
        => document.Format == BinaryFormat.T2b ? document.Rows.GroupBy(row => ("", row.Name)).ToDictionary(group => group.Key, group => group.ToArray())
            : document.Lists.SelectMany(list => list.Rows.Select(row => (Scope: list.Name, Row: row))).GroupBy(item => (item.Scope, item.Row.Name)).ToDictionary(group => group.Key, group => group.Select(item => item.Row).ToArray());

    private static Dictionary<(string Scope, string Entry), CfgRow[][]> SiblingGroups(CfgDocument document)
    {
        var result = new Dictionary<(string Scope, string Entry), List<CfgRow[]>>();
        var pending = new Stack<(IReadOnlyList<CfgRecordNode> Nodes, string Scope)>();
        pending.Push((document.GetRecordTree(), ""));
        while (pending.TryPop(out var item))
        {
            foreach (var group in item.Nodes.Where(node => node.Row is not null).GroupBy(node => node.Row!.Name))
            {
                if (!result.TryGetValue((item.Scope, group.Key), out var groups)) result[(item.Scope, group.Key)] = groups = [];
                groups.Add(group.Select(node => node.Row!).ToArray());
            }
            foreach (var node in item.Nodes)
                if (node.Children.Count > 0) pending.Push((node.Children, node.Row is null ? node.ListName! : item.Scope));
        }
        return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private static void CheckCompatibility(CfgDocument a, CfgDocument b)
    {
        if (a.Format != b.Format) throw new InvalidDataException("Both files must use the same binary format.");
        if (a.Catalog.ToText() != b.Catalog.ToText() || a.Catalog.ProfileName != b.Catalog.ProfileName) throw new InvalidDataException("Both files must use the same Structure and game profile.");
        foreach (var document in new[] { a, b })
        {
            var pending = new Stack<(CfgRecordNode Node, int Depth)>(document.GetRecordTree().Select(node => (node, 0)));
            while (pending.TryPop(out var item))
            {
                if (item.Depth > 128) throw new InvalidDataException("ConflictMerge supports up to 128 nested record blocks.");
                foreach (var child in item.Node.Children) pending.Push((child, item.Depth + 1));
            }
        }
        if (a.Format != BinaryFormat.Rdbnp) return;
        if (JsonSerializer.Serialize(a.Types) != JsonSerializer.Serialize(b.Types)
            || !a.Lists.Select(list => list.Name).Order(StringComparer.Ordinal).SequenceEqual(b.Lists.Select(list => list.Name).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Both RDBNP files must have the same native types and lists.");
        foreach (var list in a.Lists)
            if (JsonSerializer.Serialize(a.GetListFields(list.Name)) != JsonSerializer.Serialize(b.GetListFields(list.Name)))
                throw new InvalidDataException($"Native list structures differ in {list.Name}.");
    }

    private static StructureCatalog CopyCatalog(StructureCatalog catalog)
    {
        var root = StructureCatalog.Parse(catalog.ToText());
        return catalog.ProfileName is null ? root : root.ForProfile(catalog.ProfileName);
    }
}
