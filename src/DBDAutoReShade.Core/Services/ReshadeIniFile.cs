namespace DBDAutoReShade.Core.Services;

/// <summary>
/// Minimal INI document that round-trips ReShade.ini byte-for-byte: comments,
/// blank lines, ordering, casing, spacing around '=', each line's own newline
/// sequence (CRLF/LF/CR), a leading BOM character and the presence or absence
/// of a final newline are all preserved. Only lines whose value actually
/// changes are re-serialized, and then only the value part of the line.
/// ReShade-specific knowledge (which keys matter) lives in
/// <see cref="ReshadeConfigurationService"/>.
/// </summary>
public sealed class IniDocument
{
    private const char ByteOrderMark = '\uFEFF';

    private readonly List<IniLine> _lines = new();

    /// <summary>Leading U+FEFF (if the text had one), re-emitted verbatim.</summary>
    private string _prefix = string.Empty;

    /// <summary>Newline used for lines this document adds: the text's first newline, else the platform default.</summary>
    private string _newline = Environment.NewLine;

    public IReadOnlyList<IniLine> Lines => _lines;

    public static IniDocument Parse(string content)
    {
        var doc = new IniDocument();
        string text = content ?? string.Empty;
        int pos = 0;
        if (text.Length > 0 && text[0] == ByteOrderMark)
        {
            doc._prefix = ByteOrderMark.ToString();
            pos = 1;
        }

        string? firstNewline = null;
        string section = string.Empty;
        while (pos < text.Length)
        {
            int nl = text.IndexOfAny(new[] { '\r', '\n' }, pos);
            string raw;
            string ending;
            if (nl < 0)
            {
                raw = text.Substring(pos);
                ending = string.Empty;
                pos = text.Length;
            }
            else
            {
                raw = text.Substring(pos, nl - pos);
                ending = text[nl] == '\r' && nl + 1 < text.Length && text[nl + 1] == '\n'
                    ? "\r\n"
                    : text[nl].ToString();
                pos = nl + ending.Length;
                firstNewline ??= ending;
            }

            IniLine line = Classify(raw, section) with { LineEnding = ending };
            if (line.Kind == IniLineKind.Section)
            {
                section = line.Section;
            }

            doc._lines.Add(line);
        }

        doc._newline = firstNewline ?? Environment.NewLine;
        return doc;
    }

    private static IniLine Classify(string raw, string currentSection)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return new IniLine(IniLineKind.Blank, raw, string.Empty, string.Empty, string.Empty);
        }

        if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
        {
            return new IniLine(IniLineKind.Comment, raw, string.Empty, string.Empty, string.Empty);
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return new IniLine(IniLineKind.Section, raw, trimmed.Substring(1, trimmed.Length - 2).Trim(), string.Empty, string.Empty);
        }

        int eq = raw.IndexOf('=');
        if (eq < 0)
        {
            return new IniLine(IniLineKind.Comment, raw, string.Empty, string.Empty, string.Empty);
        }

        string key = raw.Substring(0, eq).Trim();
        string value = raw.Substring(eq + 1).Trim();
        return new IniLine(IniLineKind.KeyValue, raw, currentSection, key, value);
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

    /// <summary>
    /// Set a key, preserving position; creates the section/key if missing. Returns true if changed.
    /// An existing line keeps its key text, spacing around '=', trailing whitespace and newline;
    /// only the value text is replaced.
    /// </summary>
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

                _lines[i] = line with { Value = value, Raw = ReplaceValueText(line.Raw, value) };
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
                InsertLine(_lines.Count, new IniLine(IniLineKind.Blank, string.Empty, string.Empty, string.Empty, string.Empty));
            }

            InsertLine(_lines.Count, new IniLine(IniLineKind.Section, $"[{section}]", section, string.Empty, string.Empty));
            InsertLine(_lines.Count, entry);
        }
        else
        {
            int insert = sectionIndex + 1;
            while (insert < _lines.Count && _lines[insert].Kind == IniLineKind.KeyValue)
            {
                insert++;
            }

            InsertLine(insert, entry);
        }

        return true;
    }

    /// <summary>
    /// Insert a new line. Appending after a last line that has no newline gives
    /// that line the document newline and leaves the new last line without one,
    /// so the "ends with a newline" property of the original text is kept.
    /// </summary>
    private void InsertLine(int index, IniLine line)
    {
        if (index >= _lines.Count && _lines.Count > 0 && _lines[^1].LineEnding.Length == 0)
        {
            _lines[^1] = _lines[^1] with { LineEnding = _newline };
            _lines.Add(line with { LineEnding = string.Empty });
            return;
        }

        _lines.Insert(Math.Min(index, _lines.Count), line with { LineEnding = _newline });
    }

    /// <summary>Replace only the (trimmed) value span of a raw "key = value" line.</summary>
    private static string ReplaceValueText(string raw, string value)
    {
        int eq = raw.IndexOf('=');
        if (eq < 0)
        {
            return raw + "=" + value;
        }

        int start = eq + 1;
        while (start < raw.Length && char.IsWhiteSpace(raw[start]))
        {
            start++;
        }

        int end = raw.Length;
        while (end > start && char.IsWhiteSpace(raw[end - 1]))
        {
            end--;
        }

        return string.Concat(raw.AsSpan(0, start), value, raw.AsSpan(end));
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(_prefix);
        foreach (IniLine line in _lines)
        {
            sb.Append(line.Raw).Append(line.LineEnding);
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

    public sealed record IniLine(IniLineKind Kind, string Raw, string Section, string Key, string Value)
    {
        /// <summary>The newline that terminated this line in the source text ("" for a final line without one).</summary>
        internal string LineEnding { get; init; } = string.Empty;
    }
}
