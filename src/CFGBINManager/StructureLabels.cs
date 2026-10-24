using System.Globalization;
using System.Text;

namespace CFGBINManager;

public sealed record StructureLabel(BinaryFormat Format, string Entry, int ValueCount, string Path, string Name);

public sealed partial class StructureCatalog
{
    private StructureProfile? sourceProfile;
    private string sourceDeclarations = "STRUCTURE_VERSION 1;\n";
    private readonly Dictionary<(BinaryFormat Format, string Entry, int Count, string Path), string> labels = [];

    public string? GetFieldLabel(BinaryFormat format, string entry, int valueCount, string path)
    {
        RequireProfileSelection();
        return labels.GetValueOrDefault((format, ResolveEntry(format, entry), valueCount, path));
    }

    public void SetFieldLabels(IReadOnlyList<StructureLabel> changes)
    {
        RequireProfileSelection();
        var pending = new Dictionary<(BinaryFormat Format, string Entry, int Count, string Path), string>();
        foreach (var change in changes)
        {
            if (change.Format is not (BinaryFormat.T2b or BinaryFormat.Rdbnp) || string.IsNullOrWhiteSpace(change.Entry)
                || string.IsNullOrWhiteSpace(change.Name) || change.Name.Any(char.IsControl) || change.ValueCount <= 0)
                throw new ArgumentException("A field label requires a format, record name, value count and a nonempty single-line name.", nameof(changes));
            string[] parts = change.Path.Split('.');
            if (parts.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0)
                || int.Parse(parts[0], CultureInfo.InvariantCulture) >= change.ValueCount)
                throw new ArgumentException("A field label path must contain valid numeric positions within the record.", nameof(changes));
            var key = (change.Format, ResolveEntry(change.Format, change.Entry), change.ValueCount, change.Path);
            if (!pending.TryAdd(key, change.Name)) throw new ArgumentException("Duplicate field label path.", nameof(changes));
        }
        foreach (var item in pending) labels[item.Key] = item.Value;
    }

    public string ToText()
    {
        var output = new StringBuilder();
        var selected = profiles?.Values.ToArray() ?? [this];
        foreach (var catalog in selected)
        {
            if (catalog.sourceProfile is { } profile)
            {
                output.Append("GAME ").Append(CfgSyntax.Quote(profile.Name)).AppendLine(" {");
                foreach (var entry in profile.Entries)
                {
                    output.Append("    ").Append(entry.Format == BinaryFormat.T2b ? "T2B " : "RDBNP ").Append(CfgSyntax.Quote(entry.Name));
                    if (entry.Alias != entry.Name) output.Append(", ").Append(CfgSyntax.Quote(entry.Alias));
                    output.AppendLine(" {");
                    foreach (var field in entry.Fields)
                    {
                        output.Append("        ").Append(CfgSyntax.Quote(field.Name));
                        if (field.Alias is not null) output.Append(", ").Append(CfgSyntax.Quote(field.Alias));
                        output.AppendLine(";");
                    }
                    output.AppendLine("    }");
                }
                AppendDeclarations(profile.Rules);
                AppendLabels(catalog);
                output.AppendLine("}");
            }
            else { AppendDeclarations(catalog.sourceDeclarations); AppendLabels(catalog); }
        }
        return output.ToString();

        void AppendDeclarations(string declarations)
        {
            foreach (var statement in CfgSyntax.Parse(declarations).Where(statement => statement.Name is not ("LABEL" or "CHILD")))
                output.Append(statement.Name).Append(' ').AppendJoin(", ", statement.Values.Select(token => token.Quoted ? CfgSyntax.Quote(token.Text) : token.Text)).AppendLine(";");
        }
        void AppendLabels(StructureCatalog catalog)
        {
            foreach (var child in catalog.children)
                output.Append("CHILD \"T2B\", ").Append(CfgSyntax.Quote(child.Parent)).Append(", ").Append(CfgSyntax.Quote(child.Child)).AppendLine(";");
            foreach (var item in catalog.labels.OrderBy(item => item.Key.Format).ThenBy(item => item.Key.Entry, StringComparer.Ordinal).ThenBy(item => item.Key.Count).ThenBy(item => item.Key.Path, StringComparer.Ordinal))
                output.Append("LABEL ").Append(CfgSyntax.Quote(item.Key.Format == BinaryFormat.T2b ? "T2B" : "RDBNP"))
                    .Append(", ").Append(CfgSyntax.Quote(item.Key.Entry)).Append(", ").Append(item.Key.Count.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(CfgSyntax.Quote(item.Key.Path)).Append(", ").Append(CfgSyntax.Quote(item.Value)).AppendLine(";");
        }
    }
}
