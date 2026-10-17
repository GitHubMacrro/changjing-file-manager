using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Core.Operations;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Core.RecycleBin;

/// <summary>某个卷上回收站的目录布局，全部为扩展路径</summary>
public sealed class RecycleBinLayout
{
    /// <summary>卷根，普通路径</summary>
    public string VolumeRoot { get; init; } = string.Empty;

    /// <summary>回收站基础目录的扩展路径</summary>
    public string BaseDirectoryLong { get; init; } = string.Empty;

    /// <summary>当前用户子目录的扩展路径</summary>
    public string UserDirectoryLong { get; init; } = string.Empty;

    /// <summary>条目存放目录的扩展路径</summary>
    public string FilesDirectoryLong { get; init; } = string.Empty;

    /// <summary>元数据目录的扩展路径</summary>
    public string MetadataDirectoryLong { get; init; } = string.Empty;

    /// <summary>回收站基础目录的普通路径</summary>
    public string BaseDirectoryDisplay => PathHelper.ToDisplay(BaseDirectoryLong);
}

/// <summary>把条目移入回收站的结果</summary>
public readonly struct RecycleBinMoveOutcome
{
    private RecycleBinMoveOutcome(bool success, RecycleBinEntry? entry, int errorCode, string error)
    {
        Success = success;
        Entry = entry;
        ErrorCode = errorCode;
        Error = error;
    }

    public bool Success { get; }

    /// <summary>成功时的条目</summary>
    public RecycleBinEntry? Entry { get; }

    /// <summary>Win32 错误码，成功时为 0</summary>
    public int ErrorCode { get; }

    /// <summary>失败原因中文，成功时为空串</summary>
    public string Error { get; }

    /// <summary>失败时用「空」错误码表示非 Win32 失败</summary>
    public static RecycleBinMoveOutcome Ok(RecycleBinEntry entry) => new(true, entry, 0, string.Empty);

    public static RecycleBinMoveOutcome Failed(string error, int errorCode = -1)
        => new(false, null, errorCode, error);
}

/// <summary>还原 / 永久删除一个条目的结果</summary>
public readonly struct RecycleBinOutcome
{
    private RecycleBinOutcome(bool success, bool skipped, int errorCode, string error, string path)
    {
        Success = success;
        Skipped = skipped;
        ErrorCode = errorCode;
        Error = error;
        Path = path;
    }

    public bool Success { get; }

    /// <summary>是否按用户选择跳过</summary>
    public bool Skipped { get; }

    /// <summary>Win32 错误码，成功时为 0</summary>
    public int ErrorCode { get; }

    /// <summary>失败原因中文</summary>
    public string Error { get; }

    /// <summary>结果路径：还原后的普通路径 / 被永久删除的条目名</summary>
    public string Path { get; }

    public static RecycleBinOutcome Ok(string path) => new(true, false, 0, string.Empty, path);

    public static RecycleBinOutcome Skip() => new(false, true, 0, string.Empty, string.Empty);

    public static RecycleBinOutcome Failed(string error, int errorCode = -1)
        => new(false, false, errorCode, error, string.Empty);
}

/// <summary>启动时对某个卷回收站的修复报告</summary>
public sealed class RecycleBinRecoveryReport
{
    /// <summary>把「已移动但元数据未转正」的条目补写成功</summary>
    public int PromotedCount { get; internal set; }

    /// <summary>丢弃的孤立 pending，移动未发生</summary>
    public int DiscardedPendingCount { get; internal set; }

    /// <summary>缺失元数据的孤立条目，只记录不删用户数据</summary>
    public int OrphanItemCount { get; internal set; }
}

/// <summary>自有回收站存储核心：每卷隐藏系统目录，files 放原项、meta 放元数据</summary>
// 崩溃安全顺序：先写 pending、改名进 files、元数据转正，崩溃也能由 ScanAndRepair 补写
public sealed class RecycleBinStore
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
    };

    private const int MetadataVersion = 1;

    private readonly RecycleBinOptions _options;
    private readonly string _userSid;

    /// <summary>创建存储，默认选项 X:\$ChangJing.Recycle、容量 10%</summary>
    public RecycleBinStore()
        : this(new RecycleBinOptions())
    {
    }

    public RecycleBinStore(RecycleBinOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _userSid = GetCurrentUserSid();
    }

    /// <summary>当前用户 SID 字符串；取不到时用当前用户名兜底</summary>
    public string UserSid => _userSid;

    /// <summary>回收站选项即容量等，改后下次容量判断即生效</summary>
    public RecycleBinOptions Options => _options;

    /// <summary>取当前用户 SID；失败时回退为用户名</summary>
    public static string GetCurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (!string.IsNullOrEmpty(sid))
            {
                return sid!;
            }

            return identity.Name.Replace('\\', '_');
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RecycleBinStore), $"取用户 SID 失败：{ex.Message}");
            return Environment.UserName;
        }
    }

    /// <summary>解析普通路径的回收站布局；requireExists 为 true 时不建目录，不存在返回 false</summary>
    public bool TryResolveLayout(string displayPath, bool requireExists, out RecycleBinLayout layout, out string error)
    {
        layout = null!;
        error = string.Empty;

        string volumeRoot = VolumeProbe.GetMountRoot(displayPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            error = "无法确定该位置所在的卷。";
            return false;
        }

        return TryResolveLayoutForVolume(volumeRoot, requireExists, out layout, out error);
    }

    public bool TryResolveLayoutForVolume(string volumeRoot, bool requireExists, out RecycleBinLayout layout, out string error)
    {
        layout = null!;
        error = string.Empty;

        if (string.IsNullOrEmpty(volumeRoot))
        {
            error = "卷根为空。";
            return false;
        }

        string baseDisplay;
        try
        {
            string? overridden = _options.RootOverride?.Invoke(volumeRoot);
            baseDisplay = string.IsNullOrEmpty(overridden)
                ? PathHelper.Combine(volumeRoot, _options.DirectoryName)
                : overridden!;
        }
        catch (Exception ex)
        {
            error = $"回收站位置无效：{ex.Message}";
            return false;
        }

        string userDisplay = PathHelper.Combine(baseDisplay, _userSid);

        try
        {
            layout = new RecycleBinLayout
            {
                VolumeRoot = volumeRoot,
                BaseDirectoryLong = PathHelper.ToLong(baseDisplay),
                UserDirectoryLong = PathHelper.ToLong(PathHelper.Combine(userDisplay, string.Empty)),
                FilesDirectoryLong = PathHelper.ToLong(PathHelper.Combine(userDisplay, "files")),
                MetadataDirectoryLong = PathHelper.ToLong(PathHelper.Combine(userDisplay, "meta")),
            };
        }
        catch (Exception ex)
        {
            error = $"回收站路径无效：{ex.Message}";
            return false;
        }

        if (requireExists && !FileOperations.Exists(layout.UserDirectoryLong))
        {
            error = "该卷上还没有畅径回收站。";
            return false;
        }

        return true;
    }

    /// <summary>确保某个卷上的回收站目录存在，并建好 files / meta 子目录</summary>
    public bool EnsureLayout(RecycleBinLayout layout, out string error)
    {
        error = string.Empty;

        try
        {
            if (!EnsureDirectoryWithAcl(layout.BaseDirectoryLong, applyAcl: true, out error))
            {
                return false;
            }

            // 基础目录已断开继承，子目录继承即可
            if (!EnsureDirectoryWithAcl(layout.UserDirectoryLong, applyAcl: false, out error)
                || !EnsureDirectoryWithAcl(layout.FilesDirectoryLong, applyAcl: false, out error)
                || !EnsureDirectoryWithAcl(layout.MetadataDirectoryLong, applyAcl: false, out error))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecycleBinStore), ex);
            error = "创建回收站文件夹失败，详细信息已写入日志。";
            return false;
        }
    }

    private static bool EnsureDirectoryWithAcl(string longPath, bool applyAcl, out string error)
    {
        error = string.Empty;

        try
        {
            string display = PathHelper.ToDisplay(longPath);
            if (!FileOperations.Exists(longPath))
            {
                Directory.CreateDirectory(longPath);
                LogManager.Info(typeof(RecycleBinStore), $"已创建回收站目录：{display}");
            }

            if (applyAcl)
            {
                ApplyRestrictedAcl(longPath);
                HideDirectory(longPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecycleBinStore), ex);
            error = $"创建回收站文件夹失败：{PathHelper.ToDisplay(longPath)} — {ex.Message}";
            return false;
        }
    }

    private static void ApplyRestrictedAcl(string longPath)
    {
        var info = new DirectoryInfo(longPath);
        DirectorySecurity security = info.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? user = identity.User;
        if (user is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                inherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        info.SetAccessControl(security);
        LogManager.Info(typeof(RecycleBinStore), $"已设置回收站目录 ACL（断开继承、仅当前用户与 SYSTEM）：{longPath}");
    }

    private static void HideDirectory(string longPath)
    {
        try
        {
            uint current = NativeMethods.GetFileAttributesW(longPath);
            if (current == NativeMethods.InvalidFileAttributes)
            {
                return;
            }

            NativeMethods.SetFileAttributesW(
                longPath,
                current | NativeMethods.FileAttributeHidden | NativeMethods.FileAttributeSystem);
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RecycleBinStore), $"设置回收站目录隐藏属性失败：{ex.Message}");
        }
    }

    public static bool TryDescribeAcl(string longPath, out bool inheritanceBroken, out string allowedSids)
    {
        inheritanceBroken = false;
        allowedSids = string.Empty;

        try
        {
            var info = new DirectoryInfo(longPath);
            if (!info.Exists)
            {
                return false;
            }

            DirectorySecurity security = info.GetAccessControl();
            inheritanceBroken = security.AreAccessRulesProtected;

            var sids = new List<string>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow)
                {
                    sids.Add(rule.IdentityReference.Value);
                }
            }

            allowedSids = string.Join("|", sids);
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RecycleBinStore), $"读取回收站 ACL 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>该卷回收站的容量上限，按字节，0 表示不限制</summary>
    public long GetCapacityLimit(string volumeRoot)
    {
        if (_options.CapacityFixedBytes > 0)
        {
            return _options.CapacityFixedBytes;
        }

        if (_options.CapacityPercent <= 0)
        {
            return 0;
        }

        long total = VolumeProbe.GetTotalBytes(volumeRoot);
        if (total <= 0)
        {
            return 0;
        }

        double limit = total * (_options.CapacityPercent / 100.0);
        return limit >= long.MaxValue ? long.MaxValue : (long)limit;
    }

    /// <summary>该卷回收站当前占用的字节合计</summary>
    public long GetUsedBytes(string volumeRoot)
        => ListEntries(volumeRoot).Sum(entry => entry.SizeBytes);

    /// <summary>列出某个卷上的回收站条目，按删除时间倒序最新在前</summary>
    public IReadOnlyList<RecycleBinEntry> ListEntries(string volumeRoot)
    {
        if (!TryResolveLayoutForVolume(volumeRoot, requireExists: true, out RecycleBinLayout layout, out _))
        {
            return Array.Empty<RecycleBinEntry>();
        }

        var entries = new List<RecycleBinEntry>();

        foreach (string metadataLong in EnumerateMetadataFiles(layout))
        {
            RecycleBinEntry? entry = TryReadMetadata(metadataLong, layout);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => b.DeletedUtc.CompareTo(a.DeletedUtc));
        return entries;
    }

    /// <summary>列出所有卷上的回收站条目，汇总页面用，按删除时间倒序</summary>
    public IReadOnlyList<RecycleBinEntry> ListAllEntries()
    {
        var all = new List<RecycleBinEntry>();

        foreach (string volumeRoot in GetCandidateVolumeRoots())
        {
            try
            {
                all.AddRange(ListEntries(volumeRoot));
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(RecycleBinStore), $"列举回收站条目失败（{volumeRoot}）：{ex.Message}");
            }
        }

        all.Sort((a, b) => b.DeletedUtc.CompareTo(a.DeletedUtc));
        return all;
    }

    private IReadOnlyList<string> GetCandidateVolumeRoots()
    {
        if (_options.VolumeRootsOverride is not null)
        {
            try
            {
                return _options.VolumeRootsOverride() ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(RecycleBinStore), $"读取注入的卷列表失败：{ex.Message}");
                return Array.Empty<string>();
            }
        }

        var roots = new List<string>();
        foreach (DriveItem drive in LocationProvider.GetDrives())
        {
            if (drive.IsReady && drive.Kind is DriveKind.Fixed or DriveKind.Removable or DriveKind.RamDisk)
            {
                roots.Add(drive.RootPath);
            }
        }

        return roots;
    }

    /// <summary>按 ID 读取条目，不存在返回 null</summary>
    public RecycleBinEntry? TryGetEntry(string volumeRoot, string id)
    {
        if (!TryResolveLayoutForVolume(volumeRoot, requireExists: true, out RecycleBinLayout layout, out _)
            || string.IsNullOrEmpty(id))
        {
            return null;
        }

        string metadataLong = PathHelper.Combine(layout.MetadataDirectoryLong, id + ".json");
        return FileOperations.Exists(metadataLong) ? TryReadMetadata(metadataLong, layout) : null;
    }

    private static IEnumerable<string> EnumerateMetadataFiles(RecycleBinLayout layout)
    {
        try
        {
            if (!FileOperations.Exists(layout.MetadataDirectoryLong))
            {
                yield break;
            }

            foreach (string file in Directory.GetFiles(layout.MetadataDirectoryLong, "*.json"))
            {
                yield return file;
            }
        }
        finally
        {
        }
    }

    private static RecycleBinEntry? TryReadMetadata(string metadataLong, RecycleBinLayout layout)
    {
        try
        {
            string json = File.ReadAllText(metadataLong);
            MetadataDto? dto = JsonSerializer.Deserialize<MetadataDto>(json, MetadataJsonOptions);
            if (dto is null || string.IsNullOrEmpty(dto.Id))
            {
                LogManager.Warning(typeof(RecycleBinStore), $"回收站元数据内容无效：{metadataLong}");
                return null;
            }

            return new RecycleBinEntry
            {
                Id = dto.Id,
                OriginalPath = dto.OriginalPath ?? string.Empty,
                DeletedUtc = dto.DeletedUtc,
                SizeBytes = dto.SizeBytes,
                IsDirectory = dto.IsDirectory,
                ItemCount = dto.ItemCount,
                VolumeRoot = layout.VolumeRoot,
                ItemPath = PathHelper.ToDisplay(PathHelper.Combine(layout.FilesDirectoryLong, dto.Id)),
                MetadataPath = PathHelper.ToDisplay(metadataLong),
            };
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RecycleBinStore), $"读取回收站元数据失败（{metadataLong}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>把条目移入回收站：先写 pending、改名、元数据转正，完成后按容量清理</summary>
    public RecycleBinMoveOutcome MoveIn(string sourceDisplayPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDisplayPath))
        {
            return RecycleBinMoveOutcome.Failed("路径为空。");
        }

        string sourceLong;
        try
        {
            sourceLong = PathHelper.ToLong(sourceDisplayPath);
        }
        catch (Exception ex)
        {
            return RecycleBinMoveOutcome.Failed($"路径无效：{ex.Message}");
        }

        if (!FileOperations.Exists(sourceLong))
        {
            return RecycleBinMoveOutcome.Failed("源文件已不存在。", Win32ErrorMessages.ErrorFileNotFound);
        }

        if (!TryResolveLayout(sourceDisplayPath, requireExists: false, out RecycleBinLayout layout, out string layoutError))
        {
            return RecycleBinMoveOutcome.Failed(layoutError);
        }

        if (!EnsureLayout(layout, out string ensureError))
        {
            return RecycleBinMoveOutcome.Failed(ensureError);
        }

        bool isDirectory = IsDirectory(sourceLong);
        (int fileCount, long totalBytes) = MeasureEntry(sourceLong, isDirectory, cancellationToken);

        string id = Guid.NewGuid().ToString("N");
        string itemLong = PathHelper.Combine(layout.FilesDirectoryLong, id);
        string pendingLong = PathHelper.Combine(layout.MetadataDirectoryLong, id + ".json.pending");
        string finalLong = PathHelper.Combine(layout.MetadataDirectoryLong, id + ".json");

        var dto = new MetadataDto
        {
            Version = MetadataVersion,
            Id = id,
            OriginalPath = PathHelper.ToDisplay(sourceLong),
            DeletedUtc = DateTime.UtcNow,
            SizeBytes = totalBytes,
            IsDirectory = isDirectory,
            ItemCount = isDirectory ? fileCount : 1,
        };

        // 先写 pending，崩溃时据此修复
        if (!TryWriteMetadata(pendingLong, dto, out string writeError))
        {
            return RecycleBinMoveOutcome.Failed(writeError);
        }

        if (!NativeMethods.MoveFileExW(sourceLong, itemLong, dwFlags: 0))
        {
            int error = Marshal.GetLastWin32Error();
            TryDeleteFileQuietly(pendingLong);

            string reason = Win32ErrorMessages.Describe(error);
            if (error is Win32ErrorMessages.ErrorSharingViolation or Win32ErrorMessages.ErrorLockViolation)
            {
                reason = RestartManagerDescription(sourceDisplayPath) ?? reason;
            }

            LogManager.Warning(typeof(RecycleBinStore), $"移入回收站失败：{sourceDisplayPath} — {reason}（错误码 {error}）");
            return RecycleBinMoveOutcome.Failed(reason, error);
        }

        // 元数据转正
        if (!NativeMethods.MoveFileExW(pendingLong, finalLong, dwFlags: 0))
        {
            // 条目已在回收站：保留 pending，下次启动由 ScanAndRepair 补写
            LogManager.Warning(
                typeof(RecycleBinStore),
                $"回收站条目成功但元数据未转正（将在下次启动修复）：{sourceDisplayPath}");
        }

        var entry = new RecycleBinEntry
        {
            Id = id,
            OriginalPath = dto.OriginalPath,
            DeletedUtc = dto.DeletedUtc,
            SizeBytes = totalBytes,
            IsDirectory = isDirectory,
            ItemCount = dto.ItemCount,
            VolumeRoot = layout.VolumeRoot,
            ItemPath = PathHelper.ToDisplay(itemLong),
            MetadataPath = PathHelper.ToDisplay(finalLong),
        };

        LogManager.Info(
            typeof(RecycleBinStore),
            $"已移到回收站：{entry.OriginalPath} → {entry.ItemPath}（{totalBytes} 字节）");

        // 超限就自动清理最早条目，放存储层保证各路径移入都执行
        PruneByCapacity(layout.VolumeRoot);

        return RecycleBinMoveOutcome.Ok(entry);
    }

    /// <summary>把条目还原到原位置；原位置被占用时问 conflictResolver，无回调按跳过</summary>
    public RecycleBinOutcome Restore(RecycleBinEntry entry, Func<ConflictInfo, Task<ConflictDecision>>? conflictResolver)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!TryResolveLayoutForVolume(entry.VolumeRoot, requireExists: true, out RecycleBinLayout layout, out string layoutError))
        {
            return RecycleBinOutcome.Failed(layoutError);
        }

        string itemLong = PathHelper.ToLong(entry.ItemPath);
        string metadataLong = PathHelper.ToLong(entry.MetadataPath);

        if (!FileOperations.Exists(itemLong))
        {
            return RecycleBinOutcome.Failed("回收站里的项目已不存在。", Win32ErrorMessages.ErrorFileNotFound);
        }

        string originalLong;
        try
        {
            originalLong = PathHelper.ToLong(entry.OriginalPath);
        }
        catch (Exception ex)
        {
            return RecycleBinOutcome.Failed($"原路径无效：{ex.Message}");
        }

        string? parentLong = PathHelper.GetParent(originalLong);
        if (parentLong is null)
        {
            return RecycleBinOutcome.Failed("无法还原到盘根。");
        }

        // 原文件夹不存在则重建
        if (!FileOperations.Exists(parentLong))
        {
            try
            {
                Directory.CreateDirectory(parentLong);
                LogManager.Info(typeof(RecycleBinStore), $"还原时已重建原文件夹：{PathHelper.ToDisplay(parentLong)}");
            }
            catch (Exception ex)
            {
                return RecycleBinOutcome.Failed($"无法重建原文件夹：{ex.Message}");
            }
        }

        string targetLong = originalLong;

        if (FileOperations.Exists(targetLong))
        {
            bool targetIsDirectory = IsDirectory(targetLong);
            ConflictDecision decision = AskConflict(entry, targetLong, targetIsDirectory, conflictResolver);

            if (decision.Action == ConflictAction.Skip)
            {
                return RecycleBinOutcome.Skip();
            }

            if (decision.Action == ConflictAction.KeepBoth)
            {
                string name = TransferNaming.BuildKeepBothName(parentLong, PathHelper.GetName(originalLong));
                targetLong = PathHelper.Combine(parentLong, name);
            }
            else
            {
                if (entry.IsDirectory || targetIsDirectory)
                {
                    // 文件与文件夹同名不能替换：降级为跳过
                    return RecycleBinOutcome.Skip();
                }

                // 目标转为回收站条目，仍可再还原，避免直接不可逆删除
                RecycleBinMoveOutcome displaced = MoveIn(PathHelper.ToDisplay(targetLong));
                if (!displaced.Success)
                {
                    return RecycleBinOutcome.Failed($"无法替换目标：{displaced.Error}", displaced.ErrorCode);
                }
            }
        }

        if (!NativeMethods.MoveFileExW(itemLong, targetLong, dwFlags: 0))
        {
            int error = Marshal.GetLastWin32Error();
            string reason = Win32ErrorMessages.Describe(error);
            LogManager.Warning(typeof(RecycleBinStore), $"还原失败：{entry.OriginalPath} — {reason}（错误码 {error}）");
            return RecycleBinOutcome.Failed(reason, error);
        }

        TryDeleteFileQuietly(metadataLong);
        LogManager.Info(typeof(RecycleBinStore), $"已还原：{entry.OriginalPath}");
        return RecycleBinOutcome.Ok(PathHelper.ToDisplay(targetLong));
    }

    private ConflictDecision AskConflict(
        RecycleBinEntry entry,
        string targetLong,
        bool targetIsDirectory,
        Func<ConflictInfo, Task<ConflictDecision>>? conflictResolver)
    {
        if (conflictResolver is null)
        {
            return ConflictDecision.Skip();
        }

        var info = new ConflictInfo(
            PathHelper.GetName(entry.OriginalPath),
            entry.OriginalPath,
            (ulong)Math.Max(0, entry.SizeBytes),
            entry.DeletedLocal,
            PathHelper.GetName(targetLong),
            PathHelper.ToDisplay(targetLong),
            FileOperations.TryGetEntryInfo(targetLong, out _, out ulong size, out _) ? size : 0UL,
            SinceLastWrite(targetLong),
            entry.IsDirectory
                ? ConflictPairKind.DirectoryVsFile
                : targetIsDirectory ? ConflictPairKind.FileVsDirectory : ConflictPairKind.FileVsFile);

        try
        {
            return conflictResolver(info).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RecycleBinStore), $"还原冲突询问失败，按跳过处理：{ex.Message}");
            return ConflictDecision.Skip();
        }
    }

    private static DateTime SinceLastWrite(string longPath)
    {
        if (FileOperations.TryGetEntryInfo(longPath, out _, out _, out DateTime writeUtc) && writeUtc != DateTime.MinValue)
        {
            return writeUtc.ToLocalTime();
        }

        return DateTime.MinValue;
    }

    /// <summary>永久删除回收站里的条目，连同元数据</summary>
    public RecycleBinOutcome DeleteEntryPermanently(RecycleBinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string itemLong = PathHelper.ToLong(entry.ItemPath);
        string metadataLong = PathHelper.ToLong(entry.MetadataPath);

        if (FileOperations.Exists(itemLong))
        {
            OperationError? error = DeleteTree(itemLong, isDirectory: entry.IsDirectory, "<回收站>", CancellationToken.None);
            if (error is not null)
            {
                return RecycleBinOutcome.Failed(error.Reason, error.ErrorCode);
            }
        }

        TryDeleteFileQuietly(metadataLong);
        LogManager.Info(typeof(RecycleBinStore), $"已永久删除回收站条目：{entry.OriginalPath}");
        return RecycleBinOutcome.Ok(PathHelper.ToDisplay(itemLong));
    }

    /// <summary>清空某个卷上的回收站并永久删除全部条目，返回删除数</summary>
    public int Empty(string volumeRoot)
    {
        int removed = 0;
        foreach (RecycleBinEntry entry in ListEntries(volumeRoot))
        {
            if (DeleteEntryPermanently(entry).Success)
            {
                removed++;
            }
        }

        LogManager.Info(typeof(RecycleBinStore), $"已清空回收站（{volumeRoot}）：{removed} 项");
        return removed;
    }

    /// <summary>超出容量上限时自动清理最早删除的条目，返回清理数量</summary>
    public int PruneByCapacity(string volumeRoot)
    {
        long limit = GetCapacityLimit(volumeRoot);
        if (limit <= 0)
        {
            return 0;
        }

        List<RecycleBinEntry> entries = ListEntries(volumeRoot).ToList();
        long used = entries.Sum(entry => Math.Max(0, entry.SizeBytes));
        if (used <= limit)
        {
            return 0;
        }

        // 按删除时间升序逐个删除，直到降到上限以内
        entries.Sort((a, b) => a.DeletedUtc.CompareTo(b.DeletedUtc));

        int removed = 0;
        foreach (RecycleBinEntry entry in entries)
        {
            if (used <= limit)
            {
                break;
            }

            if (DeleteEntryPermanently(entry).Success)
            {
                used -= Math.Max(0, entry.SizeBytes);
                removed++;
            }
        }

        if (removed > 0)
        {
            LogManager.Info(
                typeof(RecycleBinStore),
                $"回收站超出容量上限（{limit} 字节），已自动清理最早的 {removed} 个条目（{volumeRoot}）");
        }

        return removed;
    }

    /// <summary>启动时扫描回收站：补写 pending、丢弃孤立 pending、记录缺元数据条目</summary>
    public RecycleBinRecoveryReport ScanAndRepair(string volumeRoot)
    {
        var report = new RecycleBinRecoveryReport();

        if (!TryResolveLayoutForVolume(volumeRoot, requireExists: true, out RecycleBinLayout layout, out _))
        {
            return report;
        }

        try
        {
            if (FileOperations.Exists(layout.MetadataDirectoryLong))
            {
                foreach (string pending in Directory.GetFiles(layout.MetadataDirectoryLong, "*.json.pending"))
                {
                    string fileName = Path.GetFileName(pending);
                    string id = fileName.Substring(0, fileName.Length - ".json.pending".Length);
                    string itemLong = PathHelper.Combine(layout.FilesDirectoryLong, id);
                    string finalLong = PathHelper.Combine(layout.MetadataDirectoryLong, id + ".json");

                    if (FileOperations.Exists(itemLong))
                    {
                        if (NativeMethods.MoveFileExW(PathHelper.ToLong(pending), finalLong, 0))
                        {
                            report.PromotedCount++;
                            LogManager.Info(typeof(RecycleBinStore), $"崩溃恢复：已补写回收站元数据 {id}");
                        }
                        else
                        {
                            LogManager.Warning(typeof(RecycleBinStore), $"崩溃恢复：补写元数据失败 {id}");
                        }
                    }
                    else
                    {
                        TryDeleteFileQuietly(PathHelper.ToLong(pending));
                        report.DiscardedPendingCount++;
                        LogManager.Info(typeof(RecycleBinStore), $"崩溃恢复：移动未发生，已丢弃 pending 元数据 {id}");
                    }
                }
            }

            if (FileOperations.Exists(layout.FilesDirectoryLong))
            {
                var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (FileOperations.Exists(layout.MetadataDirectoryLong))
                {
                    foreach (string metadata in Directory.GetFiles(layout.MetadataDirectoryLong, "*.json"))
                    {
                        knownIds.Add(Path.GetFileNameWithoutExtension(metadata));
                    }
                }

                foreach (string item in Directory.GetFileSystemEntries(layout.FilesDirectoryLong))
                {
                    string id = Path.GetFileName(item);
                    if (!knownIds.Contains(id))
                    {
                        report.OrphanItemCount++;
                        LogManager.Warning(
                            typeof(RecycleBinStore),
                            $"崩溃恢复：回收站条目缺少元数据（保留不删）{PathHelper.ToDisplay(item)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecycleBinStore), ex);
        }

        return report;
    }

    private static (int FileCount, long TotalBytes) MeasureEntry(string longPath, bool isDirectory, CancellationToken cancellationToken)
    {
        // 根是重解析点：只算链接自身不枚举目标，指祖先会无限递归
        if (DeleteEngine.IsReparsePoint(longPath))
        {
            return (0, 0);
        }

        if (!OperationScanner.TryScan(
                longPath,
                isDirectory,
                cancellationToken,
                out ScanTotals totals,
                out _,
                out _))
        {
            return isDirectory ? (0, 0) : (1, 0);
        }

        return (totals.FileCount, totals.TotalBytes);
    }

    private static bool TryWriteMetadata(string pendingLong, MetadataDto dto, out string error)
    {
        error = string.Empty;

        try
        {
            string json = JsonSerializer.Serialize(dto, MetadataJsonOptions);
            using var stream = new FileStream(pendingLong, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex)
        {
            LogManager.Error(typeof(RecycleBinStore), ex);
            error = $"写回收站元数据失败：{ex.Message}";
            return false;
        }
    }

    private static bool IsDirectory(string longPath)
        => FileOperations.TryGetEntryInfo(longPath, out uint attributes, out _, out _)
           && (attributes & NativeMethods.FileAttributeDirectory) != 0;

    private static void TryDeleteFileQuietly(string longPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(longPath) && FileOperations.Exists(longPath))
            {
                NativeMethods.SetFileAttributesW(longPath, NativeMethods.FileAttributeNormal);
                NativeMethods.DeleteFileW(longPath);
            }
        }
        catch
        {
        }
    }

    private static string? RestartManagerDescription(string displayPath)
    {
        try
        {
            return RestartManagerService.DescribeLockingProcesses(displayPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>迭代删除目录树，不递归不爆栈，返回第一个错误，null 表示成功</summary>
    internal static OperationError? DeleteTree(string longPath, bool isDirectory, string displayName, CancellationToken cancellationToken)
        => DeleteEngine.DeleteTreeStatic(longPath, isDirectory, displayName, cancellationToken);

    private sealed class MetadataDto
    {
        public int Version { get; set; }

        public string? Id { get; set; }

        public string? OriginalPath { get; set; }

        public DateTime DeletedUtc { get; set; }

        public long SizeBytes { get; set; }

        public bool IsDirectory { get; set; }

        public int ItemCount { get; set; }
    }
}
