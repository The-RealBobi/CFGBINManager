using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CFGBINManager;

public static partial class CfgTextCodec
{
    private const string ReadableHeader = "// Readable CFG 1";
    private const string PreservationStart = "// BEGIN RECONSTRUCTION DATA";
    private const string PreservationEnd = "// END RECONSTRUCTION DATA";
    private sealed record PositionedMetadata(int BeforeRow, string Text);
    private sealed record Preservation(string[] Names, PositionedMetadata[] Metadata);

    public static string WriteReadable(CfgDocument document)
    {
        string legacy = Write(document);
        var statements = CfgSyntax.Parse(legacy);
        var rows = document.Format == BinaryFormat.T2b ? document.Rows : document.Lists.SelectMany(list => list.Rows).ToArray();
        var metadata = new List<PositionedMetadata>();
        var names = new List<string>();
        var output = new StringBuilder(ReadableHeader + "\n// Edit values below. Field names and types are comments.\n\n");
        int rowIndex = 0;
        foreach (var statement in statements)
        {
            if (statement.Metadata)
            {
                metadata.Add(new PositionedMetadata(rowIndex, Render(statement, true)));
                if (statement.Name == "LIST") output.Append("// List: ").AppendLine(SingleLine(statement.Values[1].Text));
                continue;
            }
            var row = rows[rowIndex++];
            names.Add(statement.Name);
            output.Append(CfgSyntax.Quote(statement.Name));
            var labels = new List<string>();
            var kinds = new List<ValueKind>();
            var definitions = document.Catalog.GetFields(document.Format, row.Name, row.Values.Count);
            for (int i = 0; i < row.Values.Count; i++)
            {
                string label = definitions.Count == row.Values.Count ? definitions[i].Name : row.FieldNames[i];
                AddLabels(row.Values[i], i.ToString(Invariant), label);
            }
            if (statement.Values.Count == 0) output.AppendLine(";");
            else
            {
                output.AppendLine();
                for (int i = 0; i < statement.Values.Count; i++)
                    output.Append("    ").Append(i < kinds.Count && kinds[i] is ValueKind.Hash32 or ValueKind.Hash64 ? ulong.Parse(statement.Values[i].Text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, Invariant).ToString(Invariant) : RenderToken(statement.Values[i])).Append(i == statement.Values.Count - 1 ? ";" : ",")
                        .Append(" // ").AppendLine(i < labels.Count ? SingleLine(labels[i]) : $"Value {i}");
            }
            output.AppendLine();

            void AddLabels(CfgValue value, string path, string label)
            {
                label = document.Catalog.GetFieldLabel(document.Format, row.Name, row.Values.Count, path) ?? label;
                if (value.Kind is ValueKind.Array or ValueKind.Structure or ValueKind.Vector)
                {
                    for (int i = 0; i < value.Items.Count; i++) AddLabels(value.Items[i], path + "." + i.ToString(Invariant), label + "[" + i.ToString(Invariant) + "]");
                }
                else if (value.Kind is ValueKind.Reference16 or ValueKind.Reference32)
                {
                    labels.Add(label + ".start (" + value.Kind + ")");
                    labels.Add(label + ".count (" + value.Kind + ")");
                    kinds.Add(value.Kind); kinds.Add(value.Kind);
                }
                else { labels.Add(label + " (" + value.Kind + ")"); kinds.Add(value.Kind); }
            }
        }
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true))
            JsonSerializer.Serialize(gzip, new Preservation(names.ToArray(), metadata.ToArray()), JsonOptions);
        string encoded = Convert.ToBase64String(compressed.ToArray());
        output.AppendLine("// Keep the following section to preserve the original binary layout.").AppendLine(PreservationStart);
        for (int i = 0; i < encoded.Length; i += 100) output.Append("// ").AppendLine(encoded.Substring(i, Math.Min(100, encoded.Length - i)));
        return output.AppendLine(PreservationEnd).ToString();
    }

    private static string ExpandReadable(string text)
    {
        string[] lines = text.TrimStart('\uFEFF').Split('\n');
        bool hasHeader = lines.Any(line => line.Trim() == ReadableHeader);
        bool hasPreservation = lines.Any(line => line.Trim() == PreservationStart || line.Trim() == PreservationEnd);
        if (!hasHeader && !hasPreservation) return text;
        if (!hasHeader) throw new InvalidDataException("The readable CFG header is missing or unsupported; reconstruction data cannot be ignored.");
        int start = Array.FindIndex(lines, line => line.Trim() == PreservationStart);
        int end = Array.FindIndex(lines, line => line.Trim() == PreservationEnd);
        if (start < 1 || end <= start || lines.Skip(end + 1).Any(line => !string.IsNullOrWhiteSpace(line)))
            throw new InvalidDataException("The readable CFG reconstruction section is missing or misplaced.");
        var encoded = new StringBuilder();
        foreach (string line in lines[(start + 1)..end])
        {
            if (!line.StartsWith("// ", StringComparison.Ordinal)) throw new InvalidDataException("Invalid reconstruction data line.");
            encoded.Append(line.AsSpan(3).TrimEnd('\r'));
        }
        using var input = new MemoryStream(Convert.FromBase64String(encoded.ToString()));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        byte[] buffer = new byte[81920];
        int length;
        while ((length = gzip.Read(buffer)) != 0)
        {
            if (decoded.Length + length > 256 * 1024 * 1024) throw new InvalidDataException("CFG reconstruction data exceeds the supported size.");
            decoded.Write(buffer, 0, length);
        }
        var preservation = JsonSerializer.Deserialize<Preservation>(decoded.ToArray(), JsonOptions) ?? throw new InvalidDataException("Missing CFG reconstruction data.");
        var statements = CfgSyntax.Parse(string.Join('\n', lines[..start]));
        if (preservation.Names is null || preservation.Metadata is null || preservation.Names.Any(name => name is null)
            || preservation.Metadata.Any(item => item is null || item.Text is null) || statements.Count != preservation.Names.Length
            || statements.Where((statement, index) => statement.Metadata || statement.Name != preservation.Names[index]).Any())
            throw new InvalidDataException("Record names, count or order differ from the reconstruction data. Use the editor for structural changes.");
        var result = new StringBuilder();
        int cursor = 0;
        for (int i = 0; i <= statements.Count; i++)
        {
            while (cursor < preservation.Metadata.Length && preservation.Metadata[cursor].BeforeRow == i)
            {
                string metadataText = preservation.Metadata[cursor++].Text;
                var parsed = CfgSyntax.Parse(metadataText);
                if (parsed.Count != 1 || !parsed[0].Metadata) throw new InvalidDataException("Invalid preserved CFG declaration.");
                result.AppendLine(metadataText);
            }
            if (i < statements.Count) result.AppendLine(Render(statements[i], false));
        }
        if (cursor != preservation.Metadata.Length) throw new InvalidDataException("Reconstruction metadata is out of record order.");
        return result.ToString();
    }

    private static string Render(Statement statement, bool metadata) => (metadata ? "// @" + statement.Name : CfgSyntax.Quote(statement.Name))
        + (statement.Values.Count == 0 ? "" : " " + string.Join(", ", statement.Values.Select(RenderToken))) + ";";
    private static string RenderToken(Token token) => token.Quoted ? CfgSyntax.Quote(token.Text) : token.Text;
    private static string SingleLine(string text) => string.Concat(text.Select(character => char.IsControl(character) ? ' ' : character));
}
