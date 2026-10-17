using System.IO;
using System.Text;

namespace ChangJing.App.Core.LongPath;

/// <summary>长路径工具：普通 / 扩展路径互转、规范化、拼接</summary>
public static class PathHelper
{
    /// <summary>本地扩展路径前缀</summary>
    public const string ExtendedPrefix = @"\\?\";

    /// <summary>网络扩展路径前缀</summary>
    public const string ExtendedUncPrefix = @"\\?\UNC\";

    /// <summary>网络普通路径前缀</summary>
    public const string UncPrefix = @"\\";

    private static readonly char[] Separators = { '\\', '/' };

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    /// <summary>是否为扩展路径，以 \\?\ 开头</summary>
    public static bool IsExtended(string? path)
    {
        return !string.IsNullOrEmpty(path)
               && path!.Length >= 4
               && IsSeparator(path[0])
               && IsSeparator(path[1])
               && path[2] == '?'
               && IsSeparator(path[3]);
    }

    /// <summary>是否为网络路径，扩展与非扩展形态都算</summary>
    public static bool IsUnc(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string text = path!;
        if (IsExtended(text))
        {
            string body = text.Substring(4);
            return body.Length > 3
                   && body.StartsWith("UNC", StringComparison.OrdinalIgnoreCase)
                   && IsSeparator(body[3]);
        }

        return !IsDeviceNamespace(text)
               && text.Length >= 2
               && IsSeparator(text[0])
               && IsSeparator(text[1]);
    }

    /// <summary>是否为绝对路径，含扩展路径</summary>
    public static bool IsAbsolute(string? path) => TryParseParts(path, out _, out _);

    /// <summary>规范化后是否已是根，如 C:\</summary>
    public static bool IsRoot(string path)
    {
        if (!TryParseParts(path, out PathParts parts, out _))
        {
            return false;
        }

        return ResolveSegments(parts.Rest).Count == 0;
    }

    /// <summary>转为扩展路径并规范化；已是扩展路径同样安全</summary>
    public static string ToLong(string path) => NormalizeCore(path, forceExtended: true);

    /// <summary>尝试转为扩展路径；失败时 error 为中文原因</summary>
    public static bool TryToLong(string? path, out string extended, out string error)
    {
        extended = string.Empty;
        error = string.Empty;

        if (!TryParseParts(path, out PathParts parts, out error))
        {
            return false;
        }

        parts = new PathParts(extended: true, parts.IsUnc, parts.OpaqueVolume, parts.Root, parts.Rest);
        extended = Build(parts, ResolveSegments(parts.Rest));
        return true;
    }

    /// <summary>转为界面显示用的普通路径，失败原样返回</summary>
    public static string ToDisplay(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (!TryParseParts(path, out PathParts parts, out _))
        {
            return path!;
        }

        if (parts.OpaqueVolume)
        {
            // 卷 GUID 路径没有普通形式，保持原样
            return path!;
        }

        parts = new PathParts(extended: false, parts.IsUnc, opaqueVolume: false, parts.Root, parts.Rest);
        return Build(parts, ResolveSegments(parts.Rest));
    }

    /// <summary>规范化路径，保持扩展 / 普通形态</summary>
    public static string Normalize(string path) => NormalizeCore(path, forceExtended: null);

    /// <summary>拼接路径；绝对子路径直接采用，结尾点 / 空格保留</summary>
    public static string Combine(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return Normalize(relativePath);
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            return Normalize(basePath);
        }

        if (IsAbsolute(relativePath))
        {
            return Normalize(relativePath);
        }

        return Normalize(Normalize(basePath) + "\\" + relativePath);
    }

    /// <summary>确保路径以分隔符结尾</summary>
    public static string EnsureTrailingSeparator(string path)
    {
        string normalized = Normalize(path);
        return normalized[^1] == '\\' ? normalized : normalized + "\\";
    }

    /// <summary>取父目录；已是根时返回 null</summary>
    public static string? GetParent(string path)
    {
        if (!TryParseParts(path, out PathParts parts, out _))
        {
            return null;
        }

        List<string> segments = ResolveSegments(parts.Rest);
        if (segments.Count == 0)
        {
            return null;
        }

        segments.RemoveAt(segments.Count - 1);
        return Build(parts, segments);
    }

    /// <summary>取最后一段名称，保留结尾点 / 空格</summary>
    public static string GetName(string path)
    {
        if (!TryParseParts(path, out PathParts parts, out _))
        {
            return path;
        }

        List<string> segments = ResolveSegments(parts.Rest);
        if (segments.Count > 0)
        {
            return segments[^1];
        }

        List<string> rootSegments = SplitSegments(parts.Root);
        return rootSegments.Count > 0 ? rootSegments[^1] : parts.Root;
    }

    /// <summary>取扩展名，含点，不裁剪结尾的点 / 空格</summary>
    public static string GetExtension(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name.Substring(dot);
    }

    /// <summary>用户输入的无效字符，不含分隔符</summary>
    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>解析用户输入的路径，只做语法不访问磁盘</summary>
    public static bool TryParseUserInput(string? input, out PathParseResult result)
        => TryParseUserInput(input, aliasResolver: null, out result);

    /// <summary>解析用户输入的路径，可带别名解析</summary>
    public static bool TryParseUserInput(string? input, Func<string, string?>? aliasResolver, out PathParseResult result)
    {
        string text = (input ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            result = PathParseResult.Failed("请输入路径。");
            return false;
        }

        // 去掉成对引号，资源管理器复制文件路径会带引号
        if (text[0] == '"')
        {
            if (text.Length < 2 || text[^1] != '"')
            {
                result = PathParseResult.Failed("路径中的引号不匹配。");
                return false;
            }

            text = text.Substring(1, text.Length - 2).Trim();
            if (text.Length == 0)
            {
                result = PathParseResult.Failed("请输入路径。");
                return false;
            }
        }

        // 展开 %环境变量%；展开不了直接报错
        if (!TryExpandEnvironmentVariables(text, out text, out string envError))
        {
            result = PathParseResult.Failed(envError);
            return false;
        }

        // 别名只在输入是单个名字时尝试
        if (aliasResolver is not null && IsBareName(text))
        {
            string? resolved = aliasResolver(text);
            if (!string.IsNullOrEmpty(resolved))
            {
                text = resolved!;
            }
        }

        // C: 按 C:\ 处理，文件管理器按根更符合预期
        if (text.Length == 2 && char.IsLetter(text[0]) && text[1] == ':')
        {
            text += "\\";
        }

        if (!TryParseParts(text, out PathParts parts, out string parseError))
        {
            result = PathParseResult.Failed(parseError);
            return false;
        }

        // 逐段检查非法字符
        List<string> segments = ResolveSegments(parts.Rest);
        foreach (string segment in segments)
        {
            if (!TryValidateName(segment, out string nameError))
            {
                result = PathParseResult.Failed(nameError);
                return false;
            }
        }

        string display = Build(new PathParts(false, parts.IsUnc, parts.OpaqueVolume, parts.Root, parts.Rest), segments);
        string extended = Build(new PathParts(true, parts.IsUnc, parts.OpaqueVolume, parts.Root, parts.Rest), segments);

        result = PathParseResult.Ok(display, extended);
        return true;
    }

    /// <summary>校验名字能否被 Windows 接受，不含保留名</summary>
    public static bool TryValidateName(string name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            error = "名称不能为空。";
            return false;
        }

        if (name.Length > 255)
        {
            error = "名称过长（上限 255 个字符）。";
            return false;
        }

        foreach (char c in name)
        {
            if (c < ' ' || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                error = $"名称不能包含字符“{c}”。";
                return false;
            }
        }

        return true;
    }

    /// <summary>是否为不含分隔符、不含盘符的单个名字</summary>
    private static bool IsBareName(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (IsSeparator(c) || c == ':')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>展开 %NAME%；未定义的环境变量直接报错</summary>
    private static bool TryExpandEnvironmentVariables(string text, out string expanded, out string error)
    {
        expanded = text;
        error = string.Empty;

        int index = text.IndexOf('%');
        if (index < 0)
        {
            return true;
        }

        var builder = new StringBuilder(text.Length);
        int position = 0;

        while (position < text.Length)
        {
            char c = text[position];
            if (c != '%')
            {
                builder.Append(c);
                position++;
                continue;
            }

            int close = text.IndexOf('%', position + 1);
            if (close < 0)
            {
                // 单个 % 是合法文件名字符，原样保留
                builder.Append(c);
                position++;
                continue;
            }

            string name = text.Substring(position + 1, close - position - 1);
            if (name.Length == 0)
            {
                builder.Append("%%");
                position = close + 1;
                continue;
            }

            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                error = $"找不到环境变量 %{name}%。";
                return false;
            }

            builder.Append(value);
            position = close + 1;
        }

        expanded = builder.ToString();
        return true;
    }

    private static string NormalizeCore(string path, bool? forceExtended)
    {
        if (!TryParseParts(path, out PathParts parts, out string error))
        {
            throw new ArgumentException(error, nameof(path));
        }

        if (parts.OpaqueVolume)
        {
            // 卷 GUID / 设备路径没有普通形式，原样返回
            return path;
        }

        bool extended = forceExtended ?? parts.Extended;
        parts = new PathParts(extended, parts.IsUnc, opaqueVolume: false, parts.Root, parts.Rest);
        return Build(parts, ResolveSegments(parts.Rest));
    }

    private static bool TryParseParts(string? path, out PathParts parts, out string error)
    {
        parts = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "路径为空。";
            return false;
        }

        string text = path!;
        bool extended = IsExtended(text);

        if (extended)
        {
            return TryParseExtended(text, out parts, out error);
        }

        if (IsDeviceNamespace(text))
        {
            error = @"不支持设备路径（\\.\ 形式）。";
            return false;
        }

        if (text.Length >= 2 && IsSeparator(text[0]) && IsSeparator(text[1]))
        {
            // UNC：\\server\share\rest
            if (!TrySplitUnc(text.Substring(2), out string server, out string share, out string rest, out error))
            {
                return false;
            }

            parts = new PathParts(false, isUnc: true, opaqueVolume: false, UncPrefix + server + "\\" + share, rest);
            return true;
        }

        // 本地盘符：C:\rest
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':')
        {
            if (text.Length > 2 && !IsSeparator(text[2]))
            {
                error = "请输入完整路径（“C:”后面缺少“\\”）。";
                return false;
            }

            string rest = text.Length > 2 ? text.Substring(3) : string.Empty;
            parts = new PathParts(false, isUnc: false, opaqueVolume: false, char.ToUpperInvariant(text[0]) + ":\\", rest);
            return true;
        }

        error = "请输入完整路径（例如 C:\\Users 或 \\\\服务器\\共享）。";
        return false;
    }

    /// <summary>切分 UNC 主体为服务器名 / 共享名 / 剩余</summary>
    private static bool TrySplitUnc(string body, out string server, out string share, out string rest, out string error)
    {
        server = string.Empty;
        share = string.Empty;
        rest = string.Empty;
        error = string.Empty;

        if (body.Length == 0)
        {
            error = "网络路径不完整（缺少服务器名）。";
            return false;
        }

        int serverEnd = body.IndexOfAny(Separators);
        server = serverEnd < 0 ? body : body.Substring(0, serverEnd);
        if (server.Length == 0)
        {
            error = "网络路径不完整（缺少服务器名）。";
            return false;
        }

        if (serverEnd < 0)
        {
            error = "网络路径不完整（缺少共享名）。";
            return false;
        }

        string afterServer = body.Substring(serverEnd + 1);
        int shareEnd = afterServer.IndexOfAny(Separators);
        share = shareEnd < 0 ? afterServer : afterServer.Substring(0, shareEnd);
        if (share.Length == 0)
        {
            error = "网络路径不完整（缺少共享名）。";
            return false;
        }

        rest = shareEnd < 0 ? string.Empty : afterServer.Substring(shareEnd + 1);
        return true;
    }

    private static bool TryParseExtended(string text, out PathParts parts, out string error)
    {
        parts = default;
        error = string.Empty;

        string body = text.Substring(4);
        if (body.Length == 0)
        {
            error = "扩展路径不完整。";
            return false;
        }

        // \\?\UNC\server\share\rest
        if (body.Length >= 4
            && body.StartsWith("UNC", StringComparison.OrdinalIgnoreCase)
            && IsSeparator(body[3]))
        {
            if (!TrySplitUnc(body.Substring(4), out string server, out string share, out string rest, out error))
            {
                return false;
            }

            parts = new PathParts(true, isUnc: true, opaqueVolume: false, UncPrefix + server + "\\" + share, rest);
            return true;
        }

        // \\?\C:\rest
        if (body.Length >= 3 && char.IsLetter(body[0]) && body[1] == ':' && IsSeparator(body[2]))
        {
            string rest = body.Substring(3);
            parts = new PathParts(true, isUnc: false, opaqueVolume: false, char.ToUpperInvariant(body[0]) + ":\\", rest);
            return true;
        }

        // \\?\Volume{...} 等保留原文
        parts = new PathParts(true, isUnc: false, opaqueVolume: true, text, string.Empty);
        return true;
    }

    /// <summary>是否为 \\.\ 形式的设备命名空间</summary>
    private static bool IsDeviceNamespace(string path)
        => path.Length >= 3 && IsSeparator(path[0]) && IsSeparator(path[1]) && path[2] == '.';

    private static List<string> SplitSegments(string text)
    {
        var result = new List<string>();
        foreach (string part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(part);
        }

        return result;
    }

    /// <summary>切分并解析 . 与 ..，.. 到根后停留不动</summary>
    private static List<string> ResolveSegments(string rest)
    {
        var result = new List<string>();
        foreach (string part in SplitSegments(rest))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (result.Count > 0)
                {
                    result.RemoveAt(result.Count - 1);
                }

                continue;
            }

            result.Add(part);
        }

        return result;
    }

    private static string Build(PathParts parts, List<string> segments)
    {
        string rest = segments.Count == 0 ? string.Empty : string.Join('\\', segments);

        if (parts.OpaqueVolume)
        {
            return parts.Root + rest;
        }

        if (parts.IsUnc)
        {
            // parts.Root 形如 "\\server" 或 "\\server\share"
            string body = parts.Root.Length >= 2 ? parts.Root.Substring(2) : parts.Root;
            string combined = rest.Length == 0 ? body : body + "\\" + rest;
            return parts.Extended ? ExtendedUncPrefix + combined : UncPrefix + combined;
        }

        // 本地：parts.Root 形如 "C:\"
        return (parts.Extended ? ExtendedPrefix : string.Empty) + parts.Root + rest;
    }

    private readonly struct PathParts
    {
        public PathParts(bool extended, bool isUnc, bool opaqueVolume, string root, string rest)
        {
            Extended = extended;
            IsUnc = isUnc;
            OpaqueVolume = opaqueVolume;
            Root = root;
            Rest = rest;
        }

        /// <summary>是否为扩展形态，即以 \\?\ 开头</summary>
        public bool Extended { get; }

        public bool IsUnc { get; }

        /// <summary>是否为无普通形式的路径，Root 存原文</summary>
        public bool OpaqueVolume { get; }

        /// <summary>根：本地 C:\ 或 \\server\share</summary>
        public string Root { get; }

        /// <summary>根之后的原始剩余部分，可能含分隔符、.、..</summary>
        public string Rest { get; }
    }
}

public readonly struct PathParseResult
{
    private PathParseResult(bool success, string displayPath, string longPath, string error)
    {
        Success = success;
        DisplayPath = displayPath;
        LongPath = longPath;
        Error = error;
    }

    public bool Success { get; }

    /// <summary>规范化后的普通路径，界面显示用，永不含 \\?\</summary>
    public string DisplayPath { get; }

    /// <summary>规范化后的扩展路径，文件 API 用</summary>
    public string LongPath { get; }

    /// <summary>失败原因，中文；成功时为空串</summary>
    public string Error { get; }

    internal static PathParseResult Ok(string displayPath, string longPath) => new(true, displayPath, longPath, string.Empty);

    internal static PathParseResult Failed(string error) => new(false, string.Empty, string.Empty, error);
}
