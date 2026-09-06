namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Minimal INI document that preserves comments, blank lines, ordering and
/// original casing when ReShade.ini is edited. ReShade-specific knowledge
/// (which keys matter) lives in <see cref="ReshadeConfigurationService"/>.
/// </summary>
public sealed class IniDocument
{
    private readonly List<IniLine> _lines = new();

    public IReadOnlyList<IniLine> Lines => _lines;

    public static IniDocument Parse(string content)
    {
        var doc = new IniDocument();
        string[] rawLines = (content ?? string.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (string raw in rawLines)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                doc._lines.Add(new IniLine(IniLineKind.Blank, raw, string.Empty, string.Empty, string.Empty));
            }
            else if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                doc._lines.Add(new IniLine(IniLineKind.Comment, raw, string.Empty, string.Empty, string.Empty));
            }
            else if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                doc._lines.Add(new IniLine(IniLineKind.Section, raw, trimmed.Substring(1, trimmed.Length - 2).Trim(), string.Empty, string.Empty));
            }
            else
            {
                int eq = raw.IndexOf('=');
                if (eq < 0)
                {
                    doc._lines.Add(new IniLine(IniLineKind.Comment, raw, string.Empty, string.Empty, string.Empty));
                }
                else
                {
                    string key = raw.Substring(0, eq).Trim();
                    string value = raw.Substring(eq + 1).Trim();
                    doc._lines.Add(new IniLine(IniLineKind.KeyValue, raw, CurrentSection(doc), key, value));
                }
            }
        }

        return doc;
    }

    private static string CurrentSection(IniDocument doc)
    {
        for (int i = doc._lines.Count - 1; i >= 0; i--)
        {
            if (doc._lines[i].Kind == IniLineKind.Section)
            {
                return doc._lines[i].Section;
            }
        }

        return string.Empty;
    }

    public string? GetValue(string section, string key)
    {
        foreach (IniLine line in _lines)
        {
            if (line.Kind == IniLineKind.KeyValue &&
                string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return line.Value;
            }
        }

        return null;
    }

    /// <summary>Set a key, preserving position; creates the section/key if missing. Returns true if changed.</summary>
    public bool SetValue(string section, string key, string value)
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            IniLine line = _lines[i];
            if (line.Kind == IniLineKind.KeyValue &&
                string.Equals(line.Section, section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(line.Value, value, StringComparison.Ordinal))
                {
                    return false;
                }

                _lines[i] = line with { Value = value, Raw = $"{line.Key}={value}" };
                return true;
            }
        }

        int sectionIndex = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == IniLineKind.Section &&
                string.Equals(_lines[i].Section, section, StringComparison.OrdinalIgnoreCase))
            {
                sectionIndex = i;
            }
        }

        var entry = new IniLine(IniLineKind.KeyValue, $"{key}={value}", section, key, value);
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 &&
                !(_lines[^1].Kind == IniLineKind.Blank))
            {
                _lines.Add(new IniLine(IniLineKind.Blank, string.Empty, string.Empty, string.Empty, string.Empty));
            }

            _lines.Add(new IniLine(IniLineKind.Section, $"[{section}]", section, string.Empty, string.Empty));
            _lines.Add(entry);
        }
        else
        {
            int insert = sectionIndex + 1;
            while (insert < _lines.Count && _lines[insert].Kind == IniLineKind.KeyValue)
            {
                insert++;
            }

            _lines.Insert(insert, entry);
        }

        return true;
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        foreach (IniLine line in _lines)
        {
            sb.AppendLine(line.Kind == IniLineKind.KeyValue ? $"{line.Key}={line.Value}" : line.Raw);
        }

        return sb.ToString();
    }

    public enum IniLineKind
    {
        Blank,
        Comment,
        Section,
        KeyValue
    }

    public sealed record IniLine(IniLineKind Kind, string Raw, string Section, string Key, string Value);
}
