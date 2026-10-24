namespace CFGBINManager;

public sealed partial class CfgDocument
{
    public void MoveT2bRows(int start, int count, int destination)
    {
        RequireT2b();
        var reordered = Reorder(MutableRows, start, count, destination);
        Catalog.ValidateLinks(this);
        var edits = new List<(CfgRow Row, int Position, CfgValue Value)>();
        foreach (var rule in Catalog.RowIndices)
        {
            var oldTargets = MutableRows.Where(row => row.Name == rule.Target).ToArray();
            var positions = reordered.Where(row => row.Name == rule.Target).Select((row, index) => (row, index)).ToDictionary(item => item.row, item => item.index);
            foreach (var row in MutableRows.Where(row => row.Name == rule.Source))
            {
                int old = checked((int)row.Values[rule.Position].AsInt64());
                if (old == rule.Absent) continue;
                int next = positions[oldTargets[old]];
                if (next == rule.Absent) throw new InvalidDataException("A moved row index would collide with its absent sentinel.");
                edits.Add((row, rule.Position, CfgValue.Signed(next)));
            }
        }
        foreach (var rule in Catalog.Ranges)
        {
            var oldTargets = MutableRows.Where(row => row.Name == rule.Target).ToArray();
            var positions = reordered.Where(row => row.Name == rule.Target).Select((row, index) => (row, index)).ToDictionary(item => item.row, item => item.index);
            int[] mapping = oldTargets.Select(target => positions[target]).ToArray();
            foreach (var row in MutableRows.Where(row => row.Name == rule.Source))
            {
                int first = checked((int)row.Values[rule.StartPosition].AsInt64()), size = checked((int)row.Values[rule.CountPosition].AsInt64());
                if (size == 0) continue;
                int next = RemapRange(first, size, mapping);
                edits.Add((row, rule.StartPosition, CfgValue.Signed(next)));
            }
        }
        var candidate = new CfgDocument(Format, Catalog);
        candidate.MutableRows.AddRange(reordered);
        _ = candidate.GetRecordTree();
        _ = Catalog.CalculateCounts(candidate);
        foreach (var edit in edits) edit.Row.Assign(edit.Position, edit.Value);
        MutableRows.Clear(); MutableRows.AddRange(reordered);
    }

    public void MoveListRows(string list, int start, int count, int destination)
    {
        if (Format != BinaryFormat.Rdbnp) throw new InvalidOperationException("List moves require RDBNP.");
        RdbnpCodec.MoveRows(this, GetList(list), start, count, destination);
    }

    internal static List<CfgRow> Reorder(IReadOnlyList<CfgRow> rows, int start, int count, int destination)
    {
        if (start < 0 || count <= 0 || start > rows.Count - count || destination < 0 || destination > rows.Count)
            throw new ArgumentOutOfRangeException(nameof(start), "The moved range and destination must lie inside the document.");
        var result = rows.ToList();
        if (destination >= start && destination <= start + count) return result;
        var selected = result.GetRange(start, count); result.RemoveRange(start, count);
        result.InsertRange(destination > start ? destination - count : destination, selected);
        return result;
    }

    internal static int RemapRange(int start, int count, int[] positions)
    {
        if (start < 0 || count <= 0 || start > positions.Length - count) throw new InvalidDataException("A reference range exceeds its target list.");
        var moved = positions.AsSpan(start, count).ToArray(); Array.Sort(moved);
        if (moved[^1] - moved[0] != count - 1) throw new InvalidDataException("This move would split a referenced range into non-contiguous records.");
        return moved[0];
    }
}
