namespace CFGBINManager;

public sealed class CfgRecordNode
{
    internal readonly List<CfgRecordNode> MutableChildren = [];
    public CfgRow? Row { get; }
    public string? ListName { get; }
    public int RowIndex { get; }
    public IReadOnlyList<CfgRecordNode> Children => MutableChildren.AsReadOnly();

    internal CfgRecordNode(CfgRow? row, string? listName, int rowIndex)
    {
        Row = row; ListName = listName; RowIndex = rowIndex;
    }
}

public sealed partial class CfgDocument
{
    public string? GetBlockEnd(string entry)
    {
        string native = Catalog.ResolveEntry(BinaryFormat.T2b, entry);
        var ends = Catalog.Counts.Where(rule => rule.Start == native).Select(rule => rule.End).Distinct(StringComparer.Ordinal).ToArray();
        if (ends.Length > 1) throw new InvalidDataException($"Containment for {native} has conflicting closing entries.");
        if (ends.Length == 1) return ends[0];
        return native.EndsWith("_BEG", StringComparison.Ordinal) || native.EndsWith("_BGN", StringComparison.Ordinal) ? native[..^4] + "_END"
            : native.EndsWith("_BEGIN", StringComparison.Ordinal) || native.EndsWith("_START", StringComparison.Ordinal) ? native[..^6] + "_END" : null;
    }
}

internal static class RecordTree
{
    internal static IReadOnlyList<CfgRecordNode> Build(CfgDocument document)
    {
        var roots = new List<CfgRecordNode>();
        if (document.Format == BinaryFormat.Rdbnp)
        {
            foreach (CfgList list in document.Lists)
            {
                var node = new CfgRecordNode(null, list.Name, -1);
                for (int i = 0; i < list.Rows.Count; i++) node.MutableChildren.Add(new(list.Rows[i], list.Name, i));
                roots.Add(node);
            }
            return roots.AsReadOnly();
        }

        var openings = document.Catalog.Counts.GroupBy(rule => rule.Start).ToDictionary(group => group.Key, group =>
        {
            string[] ends = group.Select(rule => rule.End).Distinct(StringComparer.Ordinal).ToArray();
            return ends.Length == 1 ? ends[0] : throw new InvalidDataException($"Containment for {group.Key} has conflicting closing records.");
        }, StringComparer.Ordinal);
        var declared = openings.Keys.ToHashSet(StringComparer.Ordinal);
        var declaredEndings = openings.Values.Where(end => end.Length > 0).ToHashSet(StringComparer.Ordinal);
        var names = document.Rows.Select(row => row.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in names)
        {
            string? end = document.GetBlockEnd(name);
            if (end is not null && names.Contains(end) && !openings.ContainsKey(name)) openings.Add(name, end);
        }
        var endings = openings.Values.Where(end => end.Length > 0).ToHashSet(StringComparer.Ordinal);
        var pending = new Stack<(int Index, string End, bool Declared)>();
        var pairedOpenings = new HashSet<int>();
        var pairedClosings = new HashSet<int>();
        var eofOpenings = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < document.Rows.Count; i++)
        {
            CfgRow row = document.Rows[i];
            if (endings.Contains(row.Name))
            {
                // Inferred names are display hints; incomplete pairs must not reject a readable BIN.
                if (!declaredEndings.Contains(row.Name) && !pending.TakeWhile(scope => !scope.Declared).Any(scope => scope.End == row.Name)) continue;
                while (pending.Count > 0 && pending.Peek().End != row.Name && !pending.Peek().Declared) pending.Pop();
                if (pending.Count == 0 && !declaredEndings.Contains(row.Name)) continue;
                if (pending.Count == 0 || pending.Peek().End != row.Name)
                    throw new InvalidDataException($"Record {i + 1}: closing {row.Name} does not match the current containment block.");
                pairedOpenings.Add(pending.Pop().Index); pairedClosings.Add(i);
                continue;
            }
            if (!openings.TryGetValue(row.Name, out string? end)) continue;
            if (end.Length == 0 && !eofOpenings.Add(row.Name))
                throw new InvalidDataException($"End-of-file block {row.Name} has multiple openings.");
            pending.Push((i, end, declared.Contains(row.Name)));
        }
        foreach (var scope in pending)
        {
            if (scope.End.Length == 0) pairedOpenings.Add(scope.Index);
            else if (scope.Declared) throw new InvalidDataException($"Containment block {document.Rows[scope.Index].Name} has no closing {scope.End}.");
        }
        var scopes = new Stack<CfgRecordNode>();
        for (int i = 0; i < document.Rows.Count; i++)
        {
            var node = new CfgRecordNode(document.Rows[i], null, i);
            if (pairedClosings.Contains(i)) { scopes.Pop().MutableChildren.Add(node); continue; }
            (scopes.Count == 0 ? roots : scopes.Peek().MutableChildren).Add(node);
            if (pairedOpenings.Contains(i)) scopes.Push(node);
        }
        AttachCompanions(roots, document.Catalog.Children);
        return roots.AsReadOnly();
    }

    private static void AttachCompanions(List<CfgRecordNode> roots, IReadOnlyList<ChildRule> rules)
    {
        if (rules.Count == 0) return;
        var children = rules.GroupBy(rule => rule.Parent).ToDictionary(group => group.Key, group => group.Select(rule => rule.Child).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var pending = new Queue<List<CfgRecordNode>>();
        var scheduled = new HashSet<List<CfgRecordNode>> { roots };
        pending.Enqueue(roots);
        while (pending.TryDequeue(out var siblings))
        {
            CfgRecordNode[] original = siblings.ToArray();
            siblings.Clear();
            var parents = new Stack<CfgRecordNode>();
            foreach (var node in original)
            {
                while (parents.Count > 0 && !children[parents.Peek().Row!.Name].Contains(node.Row!.Name)) parents.Pop();
                (parents.Count == 0 ? siblings : parents.Peek().MutableChildren).Add(node);
                if (children.ContainsKey(node.Row!.Name)) parents.Push(node);
            }
            foreach (var node in original)
                if (node.MutableChildren.Count > 0 && scheduled.Add(node.MutableChildren)) pending.Enqueue(node.MutableChildren);
        }
    }
}
