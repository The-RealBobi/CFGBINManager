using System.Globalization;
using System.Text;

namespace CFGBINManager;

public sealed record StructureField(int Position, string Name, string? Alias);
internal sealed record StructureEntry(BinaryFormat Format, string Name, string Alias, IReadOnlyList<StructureField> Fields);
internal sealed record StructureProfile(string Name, IReadOnlyList<StructureEntry> Entries, string Rules);

internal static class StructureConfig
{
    internal static IReadOnlyList<StructureProfile> Read(string text)
    {
        var result = new List<StructureProfile>();
        string? profile = null;
        StructureEntry? entry = null;
        var entries = new List<StructureEntry>();
        var fields = new List<StructureField>();
        var rules = new StringBuilder();
        int lineNumber = 0;
        InvalidDataException Error(string message) => new($"Structure line {lineNumber}: {message}");
        Statement Statement(string value)
        {
            var statements = CfgSyntax.Parse(value);
            if (statements.Count != 1 || statements[0].Metadata) throw Error("Expected one CFG declaration.");
            return statements[0];
        }
        foreach (string raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            lineNumber++;
            string line = RemoveComment(raw).Trim();
            if (line.Length == 0) continue;
            if (entry is not null)
            {
                if (line == "}")
                {
                    entries.Add(entry with { Fields = fields.ToArray() });
                    fields.Clear(); entry = null;
                }
                else
                {
                    var field = Statement("FIELD " + line);
                    if (field.Values.Count is < 1 or > 2 || field.Values.Any(value => value.Text.Length == 0)) throw Error("A field contains its label and an optional alias.");
                    fields.Add(new StructureField(fields.Count, field.Values[0].Text, field.Values.Count == 2 ? field.Values[1].Text : null));
                }
                continue;
            }
            if (line == "}")
            {
                if (profile is null) throw Error("Unexpected closing brace.");
                result.Add(new StructureProfile(profile, entries.ToArray(), rules.ToString()));
                profile = null; entries.Clear(); rules.Clear();
            }
            else if (line.EndsWith('{'))
            {
                var header = Statement(line[..^1] + ";");
                if (profile is null)
                {
                    if (header.Name != "GAME" || header.Values.Count != 1 || header.Values[0].Text.Length == 0) throw Error("Expected GAME followed by a profile name.");
                    profile = header.Values[0].Text;
                    if (result.Any(item => item.Name == profile)) throw Error($"Duplicate profile {profile}.");
                }
                else
                {
                    if (header.Name is not ("T2B" or "RDBNP") || header.Values.Count is < 1 or > 2 || header.Values.Any(value => value.Text.Length == 0)) throw Error("Expected a T2B/RDBNP record name and optional alias.");
                    var format = header.Name == "T2B" ? BinaryFormat.T2b : BinaryFormat.Rdbnp;
                    string name = header.Values[0].Text;
                    if (entries.Any(item => item.Format == format && item.Name == name)) throw Error($"Duplicate entry {name} in {profile}/{format}.");
                    entry = new StructureEntry(format, name, header.Values.Count == 2 ? header.Values[1].Text : name, []);
                }
            }
            else if (line.EndsWith(';') && profile is not null) rules.AppendLine(line);
            else throw Error("Expected a GAME block, record block or maintenance declaration.");
        }
        if (profile is not null || entry is not null) throw Error("Unclosed structure block.");
        if (result.Count == 0) throw Error("No game profiles were declared.");
        return result;
    }

    internal static string Declarations(StructureProfile profile)
    {
        var output = new StringBuilder("STRUCTURE_VERSION 1;\n");
        foreach (StructureEntry entry in profile.Entries)
        {
            foreach (StructureField field in entry.Fields)
            {
                if (field.Alias is null || field.Alias == field.Name) continue;
                if (entry.Format == BinaryFormat.Rdbnp && entry.Fields.Any(other => other.Name == field.Alias) || entry.Format == BinaryFormat.T2b && int.TryParse(field.Alias, NumberStyles.None, CultureInfo.InvariantCulture, out int position) && position != field.Position)
                    throw new InvalidDataException($"Alias '{field.Alias}' shadows a native field in {entry.Name}.");
            }
            string format = CfgSyntax.Quote(entry.Format == BinaryFormat.T2b ? "T2B" : "RDBNP");
            string native = CfgSyntax.Quote(entry.Name);
            output.Append("ENTRY ").Append(format).Append(", ").Append(native).Append(", ").Append(CfgSyntax.Quote(entry.Alias)).AppendLine(";");
            var names = entry.Fields.SelectMany(field => field.Alias is null || field.Alias == field.Name ? new[] { field.Name } : new[] { field.Name, field.Alias }).GroupBy(name => name).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (StructureField field in entry.Fields)
            {
                string identity = entry.Format == BinaryFormat.T2b ? field.Position.ToString(CultureInfo.InvariantCulture) : CfgSyntax.Quote(field.Name);
                foreach (string alias in field.Alias is null || field.Alias == field.Name ? new[] { field.Name } : new[] { field.Name, field.Alias })
                {
                    if (names[alias] != 1 || entry.Format == BinaryFormat.T2b && int.TryParse(alias, NumberStyles.None, CultureInfo.InvariantCulture, out _) && alias != identity) continue;
                    output.Append("FIELD ").Append(format).Append(", ").Append(native).Append(", ").Append(identity).Append(", ").Append(CfgSyntax.Quote(alias)).AppendLine(";");
                }
            }
        }
        return output.Append(profile.Rules).ToString();
    }

    private static string RemoveComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (quoted && line[i] == '\\') { i++; continue; }
            if (line[i] == '"') quoted = !quoted;
            if (!quoted && i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }
}
