namespace ChangJing.App.Services.Search;

/// <summary>名称里的一段：从 Start 起 Length 个字符，是否为命中片段</summary>
public readonly record struct HighlightSegment(int Start, int Length, bool IsMatch);

/// <summary>搜索结果名称列的关键词高亮计算，纯逻辑不依赖界面</summary>
public static class SearchHighlight
{
    public static IReadOnlyList<HighlightSegment> Compute(string text, string keyword)
    {
        var segments = new List<HighlightSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (string.IsNullOrEmpty(keyword))
        {
            segments.Add(new HighlightSegment(0, text.Length, false));
            return segments;
        }

        List<(int Start, int Length)> matches = IsWildcard(keyword)
            ? FindLiteralFragments(text, keyword)
            : FindOccurrences(text, keyword);

        if (matches.Count == 0)
        {
            segments.Add(new HighlightSegment(0, text.Length, false));
            return segments;
        }

        matches.Sort((a, b) => a.Start.CompareTo(b.Start));

        int cursor = 0;
        foreach ((int start, int length) in matches)
        {
            if (start < cursor)
            {
                // 和上一段重叠，跳过
                continue;
            }

            if (start > cursor)
            {
                segments.Add(new HighlightSegment(cursor, start - cursor, false));
            }

            segments.Add(new HighlightSegment(start, length, true));
            cursor = start + length;
        }

        if (cursor < text.Length)
        {
            segments.Add(new HighlightSegment(cursor, text.Length - cursor, false));
        }

        return segments;
    }

    public static bool IsWildcard(string keyword)
        => !string.IsNullOrEmpty(keyword) && (keyword.IndexOf('*') >= 0 || keyword.IndexOf('?') >= 0);

    /// <summary>把通配符模式拆成字面片段，丢弃空片段</summary>
    public static IReadOnlyList<string> SplitLiteralFragments(string pattern)
    {
        var fragments = new List<string>();
        if (string.IsNullOrEmpty(pattern))
        {
            return fragments;
        }

        int start = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] is '*' or '?')
            {
                if (i > start)
                {
                    fragments.Add(pattern.Substring(start, i - start));
                }

                start = i + 1;
            }
        }

        if (start < pattern.Length)
        {
            fragments.Add(pattern.Substring(start));
        }

        return fragments;
    }

    private static List<(int Start, int Length)> FindOccurrences(string text, string keyword)
    {
        var result = new List<(int, int)>();
        int index = 0;

        while (index <= text.Length - keyword.Length)
        {
            int found = text.IndexOf(keyword, index, StringComparison.CurrentCultureIgnoreCase);
            if (found < 0)
            {
                break;
            }

            result.Add((found, keyword.Length));
            index = found + keyword.Length;
        }

        return result;
    }

    private static List<(int Start, int Length)> FindLiteralFragments(string text, string pattern)
    {
        var result = new List<(int, int)>();

        foreach (string fragment in SplitLiteralFragments(pattern))
        {
            if (fragment.Length == 0)
            {
                continue;
            }

            int index = 0;
            while (index <= text.Length - fragment.Length)
            {
                int found = text.IndexOf(fragment, index, StringComparison.CurrentCultureIgnoreCase);
                if (found < 0)
                {
                    break;
                }

                result.Add((found, fragment.Length));
                index = found + fragment.Length;
            }
        }

        return result;
    }
}
