using System.Globalization;
using System.Text;

namespace CFGBINManager;

internal sealed record Token(string Text, bool Quoted);
internal sealed record Statement(string Name, IReadOnlyList<Token> Values, bool Metadata);
internal static class CfgSyntax
{
    internal static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (char c in text) result.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\0' => "\\u0000", _ => c.ToString() });
        return result.Append('"').ToString();
    }
    internal static IReadOnlyList<Statement> Parse(string text)
    {
        var cleaned = new StringBuilder();
        // Metadata comments carry ordinary CFG statements and have no special escape grammar.
        foreach (string line in text.TrimStart('\uFEFF').Split('\n'))
        {
            string trimmed = line.TrimStart();
            cleaned.AppendLine(trimmed.StartsWith("// @", StringComparison.Ordinal) ? trimmed[3..] : line);
        }
        string input = cleaned.ToString();
        var statements = new List<Statement>();
        int cursor = 0;
        void Skip()
        {
            while (cursor < input.Length)
            {
                if (char.IsWhiteSpace(input[cursor])) { cursor++; continue; }
                if (cursor + 1 < input.Length && input[cursor] == '/' && input[cursor + 1] == '/')
                { while (cursor < input.Length && input[cursor] != '\n') cursor++; continue; }
                break;
            }
        }
        Token ReadToken()
        {
            Skip();
            int start = cursor;
            if (cursor == input.Length) throw new InvalidDataException("Unexpected end of CFG statement.");
            if (input[cursor] != '"')
            {
                while (cursor < input.Length && !char.IsWhiteSpace(input[cursor]) && input[cursor] != ',' && input[cursor] != ';') cursor++;
                if (start == cursor) throw new InvalidDataException($"Expected CFG token at character {cursor}.");
                return new Token(input[start..cursor], false);
            }
            cursor++;
            var value = new StringBuilder();
            while (cursor < input.Length)
            {
                char c = input[cursor++];
                if (c == '"') return new Token(value.ToString(), true);
                if (c != '\\') { value.Append(c); continue; }
                if (cursor == input.Length) break;
                c = input[cursor++];
                if (c == 'u')
                {
                    if (cursor + 4 > input.Length || !ushort.TryParse(input.AsSpan(cursor, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code)) throw new InvalidDataException("Invalid CFG Unicode escape.");
                    value.Append((char)code); cursor += 4;
                }
                else value.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', _ => throw new InvalidDataException($"Unsupported CFG escape \\{c}.") });
            }
            throw new InvalidDataException($"Unterminated CFG string at character {start}.");
        }
        while (true)
        {
            Skip(); if (cursor == input.Length) return statements;
            Token name = ReadToken();
            var values = new List<Token>();
            Skip();
            if (cursor < input.Length && input[cursor] != ';')
            {
                values.Add(ReadToken()); Skip();
                while (cursor < input.Length && input[cursor] == ',') { cursor++; values.Add(ReadToken()); Skip(); }
            }
            if (cursor == input.Length || input[cursor++] != ';') throw new InvalidDataException($"Missing semicolon after '{name.Text}'.");
            statements.Add(new Statement(!name.Quoted && name.Text.StartsWith('@') ? name.Text[1..] : name.Text, values, !name.Quoted && name.Text.StartsWith('@')));
        }
    }
    internal static long Integer(Token token)
    {
        if (token.Quoted) throw new InvalidDataException("Expected an integer, received a string.");
        if (token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return checked((long)ulong.Parse(token.Text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return long.Parse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}
