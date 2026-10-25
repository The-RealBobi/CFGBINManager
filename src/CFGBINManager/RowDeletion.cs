namespace CFGBINManager;

public sealed partial class CfgDocument
{
    public void RemoveRows(IReadOnlyCollection<CfgRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var removed = rows.ToHashSet();
        if (removed.Count == 0) return;
        var collections = Format == BinaryFormat.T2b
            ? MutableRows.GroupBy(row => row.Name).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal)
            : MutableLists.ToDictionary(list => list.Name, list => list.Rows.ToArray(), StringComparer.Ordinal);
        if (!collections.Values.SelectMany(group => group).ToHashSet().IsSupersetOf(removed))
            throw new ArgumentException("Every removed row must belong to this document.", nameof(rows));
        var keptBefore = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            var prefix = new int[collection.Value.Length + 1];
            for (int i = 0; i < collection.Value.Length; i++) prefix[i + 1] = prefix[i] + (removed.Contains(collection.Value[i]) ? 0 : 1);
            keptBefore.Add(collection.Key, prefix);
        }
        var changes = new List<(CfgRow Row, int Position, CfgValue Value)>();
        if (Format == BinaryFormat.T2b)
        {
            foreach (var row in MutableRows.Where(row => !removed.Contains(row)))
            {
                foreach (var rule in Catalog.RowIndices.Where(rule => rule.Source == row.Name))
                {
                    int old = checked((int)row.Values[rule.Position].AsInt64());
                    if (old == rule.Absent) continue;
                    int[] prefix = keptBefore.GetValueOrDefault(rule.Target, [0]);
                    if ((uint)old >= prefix.Length - 1) throw new InvalidDataException($"{row.Name}: index {old} exceeds {rule.Target}.");
                    if (prefix[old] == prefix[old + 1]) throw new InvalidDataException($"Cannot remove {rule.Target}[{old}]: a retained {row.Name}[{rule.Position}] still references it.");
                    int next = prefix[old];
                    if (next == rule.Absent) throw new InvalidDataException($"Rebased {row.Name} index would collide with its absent sentinel.");
                    changes.Add((row, rule.Position, CfgValue.Signed(next)));
                }
                foreach (var rule in Catalog.Ranges.Where(rule => rule.Source == row.Name))
                {
                    var range = Rebase(rule.Target, checked((int)row.Values[rule.StartPosition].AsInt64()), checked((int)row.Values[rule.CountPosition].AsInt64()));
                    changes.Add((row, rule.StartPosition, CfgValue.Signed(range.Start)));
                    changes.Add((row, rule.CountPosition, CfgValue.Signed(range.Count)));
                }
            }
            var preview = new CfgDocument(BinaryFormat.T2b, Catalog);
            preview.MutableRows.AddRange(MutableRows.Where(row => !removed.Contains(row)));
            _ = preview.GetRecordTree();
            foreach (var count in Catalog.CalculateCounts(preview)) changes.Add((count.Key.Row, count.Key.Position, count.Value));
        }
        else
        {
            foreach (var list in MutableLists)
            {
                if (list.Rows.Any(removed.Contains) && list.Layout!.IndexOffset != 0 && Catalog.GetIndexKey(list.Name) is null && list.Layout.IndexKey is null)
                    throw new InvalidDataException($"An INDEX_KEY declaration is required to change row count in {list.Name}.");
                var fields = RdbnpCodec.RootFields(Rdb!, list.Layout!, Catalog);
                foreach (var row in list.Rows.Where(row => !removed.Contains(row)))
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var value = RdbnpCodec.TransformReferences(this, row.Name, fields[i].Field, row.Values[i], (type, field, original) =>
                        {
                            var range = original.AsRange();
                            if (range.Count == 0) return original;
                            var rule = Catalog.References.SingleOrDefault(rule => rule.Type == type && rule.Field == field)
                                ?? throw new InvalidDataException($"Cannot remove rows: nonempty reference {type}.{field} has no REFERENCE declaration.");
                            if (!keptBefore.ContainsKey(rule.Target)) throw new InvalidDataException($"Reference target {rule.Target} is absent.");
                            var next = Rebase(rule.Target, checked((int)range.Start), checked((int)range.Count));
                            return original.Kind == ValueKind.Reference16 ? CfgValue.Range16(checked((ushort)next.Start), checked((ushort)next.Count))
                                : CfgValue.Range32(checked((uint)next.Start), checked((uint)next.Count));
                        });
                        changes.Add((row, i, value));
                    }
            }
        }
        // Validate and prepare all changes before mutating any row or collection.
        foreach (var change in changes) change.Row.Assign(change.Position, change.Value);
        MutableRows.RemoveAll(removed.Contains);
        foreach (var list in MutableLists) list.MutableRows.RemoveAll(removed.Contains);
        foreach (var row in removed) row.Document = null;

        (int Start, int Count) Rebase(string target, int start, int count)
        {
            int[] prefix = keptBefore.GetValueOrDefault(target, [0]);
            if (start < 0 || count < 0 || start > prefix.Length - 1 - count)
                throw new InvalidDataException($"Range {start}+{count} exceeds {target}.");
            return (prefix[start], prefix[start + count] - prefix[start]);
        }
    }
}
