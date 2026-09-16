using System.Text;

namespace CODDowngrader.Steam;

/// <summary>
/// A node of Valve's text KeyValues format: appmanifest_*.acf, libraryfolders.vdf.
/// Keys are matched case-insensitively, the way Steam reads them.
/// </summary>
public sealed class KvNode
{
    public string Key { get; }
    public string? Value { get; }
    public List<KvNode> Children { get; } = new();

    public KvNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public KvNode? this[string key] =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? Get(string key) => this[key]?.Value;

    /// <summary>Steam writes 64-bit IDs of 2^63 and up as negative numbers in its text files.</summary>
    public ulong GetUInt64(string key) =>
        ulong.TryParse(Get(key), out var v) ? v
        : long.TryParse(Get(key), out var signed) ? unchecked((ulong)signed)
        : 0;

    public static KvNode Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Parses a whole file. The returned node is a nameless root holding the top-level keys.</summary>
    public static KvNode Parse(string text)
    {
        var root = new KvNode("");
        var stack = new Stack<KvNode>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;

        while (NextToken(text, ref i, out var token, out var quoted))
        {
            if (!quoted && token == "{")
            {
                var node = new KvNode(pendingKey ?? "");
                stack.Peek().Children.Add(node);
                stack.Push(node);
                pendingKey = null;
            }
            else if (!quoted && token == "}")
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
            }
            else if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children.Add(new KvNode(pendingKey, token));
                pendingKey = null;
            }
        }

        return root;
    }

    static bool NextToken(string s, ref int i, out string token, out bool quoted)
    {
        token = "";
        quoted = false;

        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }

            if (c is '{' or '}')
            {
                token = c.ToString();
                i++;
                return true;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    }
                    else
                    {
                        sb.Append(s[i]);
                    }
                    i++;
                }
                i++;
                token = sb.ToString();
                quoted = true;
                return true;
            }

            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            token = s[start..i];

            // Platform conditionals such as [$WIN32] qualify the previous value; nothing here needs them.
            if (token.StartsWith('[') && token.EndsWith(']')) continue;
            return true;
        }

        return false;
    }
}
