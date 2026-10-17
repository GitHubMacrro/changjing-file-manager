using ChangJing.App.Core.LongPath;

namespace ChangJing.App.Controls;

/// <summary>地址栏面包屑的一段：显示名 + 点击后跳转的目标</summary>
public readonly record struct BreadcrumbSegment(string DisplayName, string Target, bool IsThisPc);

/// <summary>由普通路径生成地址栏面包屑的段列表</summary>
// 输入是 PathHelper.ToDisplay 的结果，所以面包屑里不含 \\?\
public static class Breadcrumbs
{
    /// <summary>由普通路径构建面包屑段，「此电脑」/「回收站」/空时只返回一段</summary>
    public static IReadOnlyList<BreadcrumbSegment> Build(
        string? displayPath,
        string thisPcName,
        string thisPcToken,
        string? recycleBinName = null,
        string? recycleBinToken = null)
    {
        // 回收站页面只显示「回收站」一段
        if (!string.IsNullOrEmpty(recycleBinToken)
            && (string.Equals(displayPath, recycleBinToken, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(recycleBinName)
                    && string.Equals(displayPath, recycleBinName, StringComparison.OrdinalIgnoreCase))))
        {
            return new[]
            {
                new BreadcrumbSegment(recycleBinName ?? "回收站", recycleBinToken!, IsThisPc: false),
            };
        }

        var segments = new List<BreadcrumbSegment>
        {
            new(thisPcName, thisPcToken, IsThisPc: true),
        };

        if (string.IsNullOrEmpty(displayPath))
        {
            return segments;
        }

        // 「此电脑」视图只有一段
        if (string.Equals(displayPath, thisPcToken, StringComparison.Ordinal)
            || string.Equals(displayPath, thisPcName, StringComparison.OrdinalIgnoreCase))
        {
            return segments;
        }

        string normalized;
        try
        {
            normalized = PathHelper.Normalize(PathHelper.ToDisplay(displayPath));
        }
        catch (ArgumentException)
        {
            // 无法解析（如卷 GUID 路径）时整条路径当一段
            segments.Add(new BreadcrumbSegment(displayPath, displayPath, IsThisPc: false));
            return segments;
        }

        // 从当前目录一路回溯到根，得到 [根, …, 当前] 的链
        var chain = new List<string>();
        string? current = normalized;
        while (!string.IsNullOrEmpty(current))
        {
            chain.Add(current);
            current = PathHelper.GetParent(current);
        }

        chain.Reverse();

        for (int i = 0; i < chain.Count; i++)
        {
            string path = chain[i];
            bool isRoot = i == 0;
            string name = isRoot ? RootDisplayName(path) : PathHelper.GetName(path);
            segments.Add(new BreadcrumbSegment(name, path, IsThisPc: false));
        }

        return segments;
    }

    /// <summary>根段显示名，本地盘 C:\ → C:，UNC 根原样</summary>
    private static string RootDisplayName(string root)
    {
        if (PathHelper.IsUnc(root))
        {
            return root;
        }

        // 本地根 C:\ → C:
        return root.TrimEnd('\\');
    }
}
