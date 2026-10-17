using System.Runtime.InteropServices;
using ChangJing.App.Core.FileSystem;
using ChangJing.App.Core.LongPath;
using ChangJing.App.Services;
using CustomWin.Utils.Logs;
using Microsoft.Win32.SafeHandles;

namespace ChangJing.App.Core.Operations;

/// <summary>复制移动引擎：先扫描再执行，支持暂停取消等</summary>
// 文件 IO 走扩展路径；复制先写临时名再改名
public static class OperationEngine
{
    private enum StepResult
    {
        Success,

        Skipped,

        Failed,
    }

    /// <summary>执行任务返回汇总结果，不抛异常</summary>
    // 同步阻塞，队列放后台线程执行
    public static OperationResult Execute(OperationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var context = new ExecutionContext(job);
        return context.Run();
    }

    private sealed class ExecutionContext
    {
        private readonly OperationJob _job;
        private readonly OperationKind _kind;
        private readonly bool _isMove;
        private readonly bool _forceCrossVolume;
        private readonly OperationProgressTracker _tracker;
        private readonly List<OperationError> _errors = new();

        private int _succeeded;
        private int _skipped;
        private ConflictDecision? _applyToAll;
        private readonly Dictionary<string, long> _bytesToCopyPerVolume = new(StringComparer.OrdinalIgnoreCase);
        private string _targetDirectoryDisplayPath = string.Empty;

        public ExecutionContext(OperationJob job)
        {
            _job = job;
            _kind = job.Kind;
            _isMove = job.Kind == OperationKind.Move;
            _forceCrossVolume = job.Options.ForceCrossVolume;
            _tracker = new OperationProgressTracker(job.Kind, () => _job.State, job.ProgressReporter);

            // 状态变化立即汇报，不必等到文件边界
            _job.StateChanged += (_, _) => _tracker.SetState(force: true);
        }

        public OperationResult Run()
        {
            try
            {
                if (_job.Sources.Count == 0)
                {
                    _job.SetState(OperationState.Failed);
                    return Finish(OperationState.Failed, "没有要处理的项目。");
                }

                _targetDirectoryDisplayPath = _job.Sources[0].TargetDirectoryDisplayPath;

                LogManager.Info(
                    typeof(OperationEngine),
                    $"开始{KindText}任务：{_job.Sources.Count} 个源 → {_targetDirectoryDisplayPath}");

                if (!Scan())
                {
                    return Finish(OperationState.Canceled, $"已取消（扫描阶段）。");
                }

                // 永久删除没有复制/移动语义，单独走一条路径
                if (_kind == OperationKind.Delete)
                {
                    return RunDelete();
                }

                if (!CheckFreeSpace(out string spaceError))
                {
                    _job.SetState(OperationState.Failed);
                    LogManager.Warning(typeof(OperationEngine), $"任务未开始（{KindText}）：{spaceError}");
                    return Finish(OperationState.Failed, spaceError);
                }

                _job.SetState(OperationState.Running);
                _tracker.SetState(force: true);

                bool canceled = false;
                foreach (OperationSource source in _job.Sources)
                {
                    if (_job.CancellationToken.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }

                    ProcessSource(source);
                }

                _tracker.Flush();

                if (canceled || _job.CancellationToken.IsCancellationRequested)
                {
                    _job.SetState(OperationState.Canceled);
                    return Finish(OperationState.Canceled, BuildSummary("已取消"));
                }

                _job.SetState(OperationState.Completed);
                return Finish(OperationState.Completed, BuildSummary(null));
            }
            catch (Exception ex)
            {
                LogManager.Error(typeof(OperationEngine), ex);
                _job.SetState(OperationState.Failed);
                return Finish(OperationState.Failed, "操作过程中发生错误，详细信息已写入日志。");
            }
        }

        private string KindText => _kind switch
        {
            OperationKind.Move => "移动",
            OperationKind.Delete => "删除",
            _ => "复制",
        };

        private OperationResult RunDelete()
        {
            _job.SetState(OperationState.Running);
            _tracker.SetState(force: true);

            bool canceled = false;

            foreach (OperationSource source in _job.Sources)
            {
                if (_job.CancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                if (!_job.WaitIfPaused())
                {
                    canceled = true;
                    break;
                }

                if (!TryResolveSource(
                        source,
                        out string sourceLong,
                        out string sourceDisplay,
                        out bool isDirectory,
                        out string resolveError))
                {
                    RecordFailure(source.SourceDisplayPath, source.SourceDisplayPath, resolveError, -1, source);
                    continue;
                }

                _tracker.BeginItem(PathHelper.GetName(sourceLong));

                DeleteTreeResult deleteResult = DeleteEngine.DeleteTree(
                    sourceLong,
                    isDirectory,
                    PathHelper.GetName(sourceLong),
                    _job.CancellationToken,
                    (name, bytes) =>
                    {
                        _tracker.BeginItem(name);
                        _tracker.CompleteItem(bytes);
                    });

                if (deleteResult.Canceled)
                {
                    canceled = true;
                    break;
                }

                if (deleteResult.Errors.Count > 0)
                {
                    foreach (OperationError error in deleteResult.Errors)
                    {
                        _errors.Add(error);
                    }

                    continue;
                }

                // 空文件夹没有文件，这里补一次完成计数
                if (isDirectory && deleteResult.DeletedFiles == 0)
                {
                    _tracker.CompleteItem(0);
                }

                _succeeded++;
            }

            _tracker.Flush();

            if (canceled || _job.CancellationToken.IsCancellationRequested)
            {
                _job.SetState(OperationState.Canceled);
                return Finish(OperationState.Canceled, BuildSummary("已取消"));
            }

            _job.SetState(OperationState.Completed);
            return Finish(OperationState.Completed, BuildSummary(null));
        }

        private OperationResult Finish(OperationState state, string message)
        {
            _job.SetState(state);
            _tracker.SetState(force: true);

            var result = new OperationResult(
                _kind,
                state,
                _targetDirectoryDisplayPath,
                _succeeded,
                _skipped,
                _errors,
                message);

            LogManager.Info(
                typeof(OperationEngine),
                $"{KindText}任务结束：状态={state}，成功={_succeeded}，跳过={_skipped}，失败={_errors.Count}");

            return result;
        }

        private string BuildSummary(string? prefix)
        {
            string body = $"{KindText}完成：成功 {_succeeded} 项，跳过 {_skipped} 项";
            if (_errors.Count > 0)
            {
                body += $"，失败 {_errors.Count} 项";
            }

            body += "。";
            return prefix is null ? body : $"{prefix}。{body}";
        }

        private bool Scan()
        {
            _job.SetState(OperationState.Scanning);
            _tracker.SetState(force: true);

            int totalFiles = 0;
            long totalBytes = 0;

            foreach (OperationSource source in _job.Sources)
            {
                if (_job.CancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (!_job.WaitIfPaused())
                {
                    return false;
                }

                if (!TryResolveSource(source, out string sourceLong, out string sourceDisplay, out bool isDirectory, out string resolveError))
                {
                    // 扫描只统计，错误统一在执行阶段记录一次
                    LogManager.Info(typeof(OperationEngine), $"扫描跳过（{source.SourceDisplayPath}）：{resolveError}");
                    continue;
                }

                _tracker.BeginItem(PathHelper.GetName(sourceLong));

                bool scanned = OperationScanner.TryScan(
                    sourceLong,
                    isDirectory,
                    _job.CancellationToken,
                    out ScanTotals totals,
                    out bool canceled,
                    out string scanError);

                if (canceled)
                {
                    return false;
                }

                if (!scanned)
                {
                    LogManager.Info(typeof(OperationEngine), $"扫描未完成（{sourceDisplay}）：{scanError}");
                    continue;
                }

                totalFiles += totals.FileCount;
                totalBytes += totals.TotalBytes;

                if (NeedsCopyingBytes(source, sourceLong))
                {
                    string mount = ResolveTargetMount(source);
                    _bytesToCopyPerVolume[mount] = _bytesToCopyPerVolume.TryGetValue(mount, out long existing)
                        ? existing + totals.TotalBytes
                        : totals.TotalBytes;
                }

                _tracker.SetTotals(totalFiles, totalBytes);
            }

            _tracker.SetTotals(totalFiles, totalBytes);
            return true;
        }

        private bool NeedsCopyingBytes(OperationSource source, string sourceLong)
        {
            if (!_isMove)
            {
                return true;
            }

            // 强制跨卷仅验证用，按跨卷处理
            if (_forceCrossVolume)
            {
                return true;
            }

            if (!TryResolveTarget(source, out string targetLong, out _))
            {
                return true;
            }

            // 同卷移动 = 改名，不占空间
            return !TransferVolume.IsSameVolume(sourceLong, targetLong);
        }

        private bool IsSameVolumeMove(string sourceLong, string targetLong)
            => _isMove && !_forceCrossVolume && TransferVolume.IsSameVolume(sourceLong, targetLong);

        private string ResolveTargetMount(OperationSource source)
        {
            if (!TryResolveTarget(source, out string targetLong, out _))
            {
                return "::unknown";
            }

            string mount = TransferVolume.GetMountRoot(targetLong);
            return mount.Length > 0 ? mount : "::unknown";
        }

        private bool CheckFreeSpace(out string error)
        {
            error = string.Empty;

            foreach ((string mount, long needed) in _bytesToCopyPerVolume)
            {
                if (needed <= 0 || mount == "::unknown")
                {
                    continue;
                }

                ulong free = OperationScanner.GetAvailableFreeSpace(mount);
                if (free > 0 && free < (ulong)needed)
                {
                    error = $"目标磁盘空间不足：需要 {FormatSize(needed)}，可用 {FormatSize((long)free)}。";
                    return false;
                }
            }

            return true;
        }

        private void ProcessSource(OperationSource source)
        {
            if (!_job.WaitIfPaused())
            {
                return;
            }

            if (!TryResolveSource(source, out string sourceLong, out string sourceDisplay, out bool isDirectory, out string resolveError))
            {
                RecordFailure(source.SourceDisplayPath, source.SourceDisplayPath, resolveError, -1, source);
                return;
            }

            if (!TryResolveTarget(source, out string targetDirLong, out string targetDirDisplay))
            {
                RecordFailure(sourceDisplay, sourceDisplay, "目标位置不可用。", -1, source);
                return;
            }

            string name = PathHelper.GetName(sourceLong);

            // 不允许把文件夹复制/移动到自身或其子文件夹
            if (isDirectory && IsSameOrDescendantPath(targetDirLong, sourceLong))
            {
                const string selfError = "不允许把文件夹复制或移动到自身或其子文件夹。";
                RecordFailure(sourceDisplay, sourceDisplay, selfError, -1, source);
                return;
            }

            string? parent = PathHelper.GetParent(sourceLong);
            bool sameDirectory = parent is not null
                && string.Equals(PathHelper.Normalize(parent), PathHelper.Normalize(targetDirLong), StringComparison.OrdinalIgnoreCase);

            string? forcedTargetName = null;

            if (sameDirectory)
            {
                if (_isMove)
                {
                    // 移动到原目录等于无操作
                    _skipped++;
                    LogManager.Info(typeof(OperationEngine), $"移动跳过（已在目标目录）：{sourceDisplay}");
                    return;
                }

                // 复制到同一目录自动命名「X - 副本」
                forcedTargetName = TransferNaming.BuildCopyName(targetDirLong, name);
            }

            _tracker.BeginItem(name);

            StepResult outcome = ProcessEntry(
                sourceLong,
                sourceDisplay,
                name,
                forcedTargetName,
                targetDirLong);

            switch (outcome)
            {
                case StepResult.Success:
                    _succeeded++;
                    break;
                case StepResult.Skipped:
                    _skipped++;
                    break;
                case StepResult.Failed:
                    // 失败项已在内部记录，这里只做计数
                    break;
            }
        }

        private StepResult ProcessEntry(
            string sourceLong,
            string sourceDisplay,
            string sourceName,
            string? forcedTargetName,
            string targetDirLong)
        {
            if (!_job.WaitIfPaused())
            {
                return StepResult.Failed;
            }

            if (!TryGetMeta(sourceLong, out EntryMeta meta))
            {
                RecordFailure(sourceDisplay, sourceDisplay, "源文件已不存在。", Win32ErrorMessages.ErrorFileNotFound, null);
                return StepResult.Failed;
            }

            return meta.IsDirectory
                ? ProcessDirectory(sourceLong, sourceDisplay, sourceName, forcedTargetName, meta, targetDirLong)
                : ProcessFile(sourceLong, sourceDisplay, sourceName, forcedTargetName, meta, targetDirLong);
        }

        private StepResult ProcessFile(
            string sourceLong,
            string sourceDisplay,
            string sourceName,
            string? forcedTargetName,
            EntryMeta meta,
            string targetDirLong)
        {
            string targetName = forcedTargetName ?? sourceName;
            string targetLong = PathHelper.Combine(targetDirLong, targetName);
            bool replace = false;

            if (forcedTargetName is null && FileOperations.Exists(targetLong))
            {
                bool targetIsDirectory = IsDirectory(targetLong);
                var (decision, resolvedName) = ResolveConflict(
                    sourceLong,
                    sourceDisplay,
                    sourceName,
                    meta,
                    targetLong,
                    targetIsDirectory);

                if (decision == ConflictAction.Skip)
                {
                    _tracker.SkipItem();
                    LogManager.Info(typeof(OperationEngine), $"{KindText}跳过（同名冲突）：{sourceDisplay}");
                    return StepResult.Skipped;
                }

                if (decision == ConflictAction.KeepBoth)
                {
                    targetName = resolvedName ?? TransferNaming.BuildKeepBothName(targetDirLong, sourceName);
                    targetLong = PathHelper.Combine(targetDirLong, targetName);
                }
                else
                {
                    replace = true;
                }
            }

            _tracker.BeginItem(sourceName);

            if (IsSameVolumeMove(sourceLong, targetDirLong))
            {
                return MoveFileSameVolume(sourceLong, sourceDisplay, targetLong, replace);
            }

            // 复制，或跨卷移动的复制阶段
            if (!TryCopyFile(sourceLong, sourceDisplay, (long)meta.Size, targetDirLong, targetName, replace, out int copyError))
            {
                // 取消导致的失败不计入错误列表
                if (_job.CancellationToken.IsCancellationRequested)
                {
                    return StepResult.Failed;
                }

                RecordFailure(sourceDisplay, sourceDisplay, ReasonFor(copyError, sourceDisplay, isTarget: false), copyError, null);
                return StepResult.Failed;
            }

            _tracker.CompleteItem((long)meta.Size);

            if (_isMove)
            {
                // 跨卷移动：复制成功且大小一致后再删源
                if (!TryDeleteSourceFile(sourceLong, sourceDisplay, out int deleteError))
                {
                    RecordFailure(sourceDisplay, sourceDisplay, ReasonFor(deleteError, sourceDisplay, isTarget: false), deleteError, null);
                    return StepResult.Failed;
                }
            }

            return StepResult.Success;
        }

        private StepResult ProcessDirectory(
            string sourceLong,
            string sourceDisplay,
            string sourceName,
            string? forcedTargetName,
            EntryMeta meta,
            string targetDirLong)
        {
            string targetName = forcedTargetName ?? sourceName;
            string targetLong = PathHelper.Combine(targetDirLong, targetName);

            // 同卷移动且目标不存在 = 单次改名
            if (_isMove
                && forcedTargetName is null
                && IsSameVolumeMove(sourceLong, targetDirLong)
                && !FileOperations.Exists(targetLong))
            {
                if (NativeMethods.MoveFileExW(sourceLong, targetLong, dwFlags: 0))
                {
                    _tracker.CompleteItem(0);
                    return StepResult.Success;
                }

                int error = Marshal.GetLastWin32Error();
                RecordFailure(sourceDisplay, sourceDisplay, ReasonFor(error, sourceDisplay, false), error, null);
                return StepResult.Failed;
            }

            bool targetExisted = FileOperations.Exists(targetLong);
            bool merge = false;

            if (forcedTargetName is null && targetExisted)
            {
                if (IsDirectory(targetLong))
                {
                    // 同名文件夹 = 合并，不询问
                    merge = true;
                    LogManager.Info(typeof(OperationEngine), $"合并文件夹：{sourceDisplay} → {PathHelper.ToDisplay(targetLong)}");
                }
                else
                {
                    // 文件夹与文件同名冲突不能替换
                    var (decision, resolvedName) = ResolveConflict(
                        sourceLong,
                        sourceDisplay,
                        sourceName,
                        meta,
                        targetLong,
                        targetIsDirectory: false);

                    if (decision == ConflictAction.Skip)
                    {
                        _tracker.SkipItem();
                        return StepResult.Skipped;
                    }

                    targetName = resolvedName ?? TransferNaming.BuildKeepBothName(targetDirLong, sourceName);
                    targetLong = PathHelper.Combine(targetDirLong, targetName);
                }
            }

            _tracker.BeginItem(sourceName);

            if (!merge)
            {
                if (!NativeMethods.CreateDirectoryW(targetLong, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != Win32ErrorMessages.ErrorAlreadyExists)
                    {
                        RecordFailure(sourceDisplay, sourceDisplay, ReasonFor(error, PathHelper.ToDisplay(targetLong), isTarget: true), error, null);
                        return StepResult.Failed;
                    }
                }
            }

            // 递归处理子项
            bool anyFailure = false;
            var enumerator = new DirectoryEnumerator();
            var (children, enumResult) = enumerator.ToList(sourceLong, _job.CancellationToken);

            if (enumResult.Canceled)
            {
                return StepResult.Failed;
            }

            if (!enumResult.Success)
            {
                RecordFailure(sourceDisplay, sourceDisplay, enumResult.Error, enumResult.ErrorCode, null);
                anyFailure = true;
            }

            foreach (FileEntry child in children)
            {
                if (_job.CancellationToken.IsCancellationRequested)
                {
                    return StepResult.Failed;
                }

                string childSourceDisplay = PathHelper.ToDisplay(child.FullPath);
                StepResult childResult = ProcessEntry(
                    child.FullPath,
                    childSourceDisplay,
                    child.Name,
                    forcedTargetName: null,
                    targetLong);

                if (childResult == StepResult.Failed)
                {
                    anyFailure = true;
                }
            }

            if (anyFailure)
            {
                // 有子项失败不删源、不补时间戳
                return StepResult.Failed;
            }

            // 时间戳放最后补，避免被子项刷新
            FinalizeDirectory(targetLong, sourceLong, meta);

            if (_isMove)
            {
                // 子项都处理完才删源，跳过则保留
                NativeMethods.RemoveDirectoryW(sourceLong);
            }

            _tracker.CompleteItem(0);
            return StepResult.Success;
        }

        private bool TryCopyFile(
            string sourceLong,
            string sourceDisplay,
            long sourceSize,
            string targetDirLong,
            string targetName,
            bool replace,
            out int errorCode)
        {
            errorCode = 0;

            string finalPath = PathHelper.Combine(targetDirLong, targetName);
            string tempPath = TransferNaming.BuildTempPath(targetDirLong, targetName);

            int cancelFlag = 0;
            NativeMethods.CopyProgressRoutine callback = (total, transferred, _, _, _, _, _, _, _) =>
            {
                if (!_job.WaitIfPaused())
                {
                    return NativeMethods.ProgressCancel;
                }

                _tracker.ReportWithinFile(transferred);
                return NativeMethods.ProgressContinue;
            };

            bool ok;
            try
            {
                ok = NativeMethods.CopyFileExW(
                    sourceLong,
                    tempPath,
                    callback,
                    IntPtr.Zero,
                    ref cancelFlag,
                    NativeMethods.CopyFileFailIfExists);
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(OperationEngine), $"复制文件异常（{sourceDisplay}）：{ex.Message}");
                SafeDeleteFile(tempPath);
                errorCode = -1;
                return false;
            }

            if (!ok)
            {
                errorCode = Marshal.GetLastWin32Error();

                // 取消时 CopyFileExW 返回 false，不算失败
                if (_job.CancellationToken.IsCancellationRequested)
                {
                    SafeDeleteFile(tempPath);
                    errorCode = 0;
                    return false;
                }

                SafeDeleteFile(tempPath);
                return false;
            }

            // 校验大小一致才落最终名，失败不丢旧文件
            if (!TryGetMeta(tempPath, out EntryMeta copied) || (long)copied.Size != sourceSize)
            {
                SafeDeleteFile(tempPath);
                LogManager.Warning(
                    typeof(OperationEngine),
                    $"复制后大小不一致，已删除临时文件：{sourceDisplay}（源 {sourceSize} 字节）");
                errorCode = Win32ErrorMessages.ErrorWriteFault;
                return false;
            }

            if (!replace && FileOperations.Exists(finalPath))
            {
                SafeDeleteFile(tempPath);
                errorCode = Win32ErrorMessages.ErrorAlreadyExists;
                return false;
            }

            uint flags = replace ? NativeMethods.MoveFileReplaceExisting : 0u;
            if (!NativeMethods.MoveFileExW(tempPath, finalPath, flags))
            {
                errorCode = Marshal.GetLastWin32Error();
                SafeDeleteFile(tempPath);
                return false;
            }

            // CopyFileExW 已保留时间戳，这里再显式设置
            TryApplyTimesAndAttributes(finalPath, tempPath: null, meta: null, sourceLong: sourceLong);
            return true;
        }

        private StepResult MoveFileSameVolume(string sourceLong, string sourceDisplay, string targetLong, bool replace)
        {
            _tracker.BeginItem(PathHelper.GetName(sourceLong));

            uint flags = replace ? NativeMethods.MoveFileReplaceExisting : 0u;
            if (NativeMethods.MoveFileExW(sourceLong, targetLong, flags))
            {
                _tracker.CompleteItem(0);
                return StepResult.Success;
            }

            int error = Marshal.GetLastWin32Error();
            RecordFailure(sourceDisplay, sourceDisplay, ReasonFor(error, sourceDisplay, false), error, null);
            return StepResult.Failed;
        }

        private (ConflictAction Action, string? ResolvedName) ResolveConflict(
            string sourceLong,
            string sourceDisplay,
            string sourceName,
            EntryMeta sourceMeta,
            string targetLong,
            bool targetIsDirectory)
        {
            bool canReplace = !sourceMeta.IsDirectory && !targetIsDirectory;
            ConflictPairKind pairKind = sourceMeta.IsDirectory
                ? ConflictPairKind.DirectoryVsFile
                : targetIsDirectory ? ConflictPairKind.FileVsDirectory : ConflictPairKind.FileVsFile;

            // 对剩余冲突套用仅在合法时生效
            if (_applyToAll is { } applied)
            {
                if (applied.Action != ConflictAction.Replace || canReplace)
                {
                    return (applied.Action, applied.Action == ConflictAction.KeepBoth
                        ? TransferNaming.BuildKeepBothName(PathHelper.GetParent(targetLong) ?? targetLong, sourceName)
                        : null);
                }
            }

            Func<ConflictInfo, Task<ConflictDecision>>? resolver = _job.ConflictResolver;
            if (resolver is null)
            {
                // 没有界面提供者时出于安全默认跳过
                return (ConflictAction.Skip, null);
            }

            TryGetMeta(targetLong, out EntryMeta targetMeta);

            var info = new ConflictInfo(
                sourceName,
                sourceDisplay,
                sourceMeta.Size,
                ToLocal(sourceMeta.LastWriteTimeUtc),
                PathHelper.GetName(targetLong),
                PathHelper.ToDisplay(targetLong),
                targetMeta.Size,
                ToLocal(targetMeta.LastWriteTimeUtc),
                pairKind);

            ConflictDecision decision;
            try
            {
                // 同步等界面答复，界面在自己的线程弹窗
                decision = resolver(info).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(OperationEngine), $"冲突询问失败，按跳过处理：{ex.Message}");
                return (ConflictAction.Skip, null);
            }

            ConflictAction action = decision.Action;
            if (action == ConflictAction.Replace && !canReplace)
            {
                // 文件与文件夹同名不能替换，降级为跳过
                action = ConflictAction.Skip;
            }

            if (decision.ApplyToAll)
            {
                _applyToAll = new ConflictDecision(action, applyToAll: true);
            }

            string? resolved = action == ConflictAction.KeepBoth
                ? TransferNaming.BuildKeepBothName(PathHelper.GetParent(targetLong) ?? targetLong, sourceName)
                : null;

            return (action, resolved);
        }

        private bool TryDeleteSourceFile(string sourceLong, string sourceDisplay, out int errorCode)
        {
            if (NativeMethods.DeleteFileW(sourceLong))
            {
                errorCode = 0;
                return true;
            }

            errorCode = Marshal.GetLastWin32Error();

            // 只读文件删不掉，去掉只读属性后重试一次
            if (errorCode == Win32ErrorMessages.ErrorAccessDenied
                && NativeMethods.SetFileAttributesW(sourceLong, NativeMethods.FileAttributeNormal))
            {
                if (NativeMethods.DeleteFileW(sourceLong))
                {
                    errorCode = 0;
                    return true;
                }

                errorCode = Marshal.GetLastWin32Error();
            }

            return false;
        }

        private static void FinalizeDirectory(string targetLong, string sourceLong, EntryMeta meta)
        {
            TryApplyTimesAndAttributes(targetLong, tempPath: null, meta: meta, sourceLong: sourceLong);
        }

        private static void TryApplyTimesAndAttributes(string targetLong, string? tempPath, EntryMeta? meta, string sourceLong)
        {
            if (meta is null)
            {
                if (!TryGetMeta(sourceLong, out EntryMeta sourceMeta))
                {
                    return;
                }

                meta = sourceMeta;
            }

            EntryMeta m = meta.Value;

            // 先设时间再设属性，只读文件也允许
            SafeFileHandle? handle = OpenForAttributes(targetLong);
            if (handle is not null)
            {
                using (handle)
                {
                    NativeMethods.SetFileTimes(handle, m.CreationTimeUtc, m.LastAccessTimeUtc, m.LastWriteTimeUtc);
                }
            }

            // 只设 SetFileAttributesW 能写的位，其余会失败
            uint settableMask = NativeMethods.FileAttributeReadOnly
                | NativeMethods.FileAttributeHidden
                | NativeMethods.FileAttributeSystem
                | NativeMethods.FileAttributeArchive
                | NativeMethods.FileAttributeNotContentIndexed
                | NativeMethods.FileAttributeTemporary;

            uint attributes = m.Attributes & settableMask;
            if (attributes == 0)
            {
                attributes = NativeMethods.FileAttributeNormal;
            }

            NativeMethods.SetFileAttributesW(targetLong, attributes);
        }

        private static SafeFileHandle? OpenForAttributes(string longPath)
        {
            try
            {
                SafeFileHandle handle = NativeMethods.CreateFileW(
                    longPath,
                    NativeMethods.FileWriteAttributes,
                    NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                    IntPtr.Zero,
                    NativeMethods.OpenExisting,
                    NativeMethods.FileFlagBackupSemantics,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return handle;
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(OperationEngine), $"打开属性句柄失败：{ex.Message}");
                return null;
            }
        }

        private readonly struct EntryMeta
        {
            public EntryMeta(bool isDirectory, ulong size, uint attributes, DateTime creation, DateTime access, DateTime write)
            {
                IsDirectory = isDirectory;
                Size = size;
                Attributes = attributes;
                CreationTimeUtc = creation;
                LastAccessTimeUtc = access;
                LastWriteTimeUtc = write;
            }

            public bool IsDirectory { get; }

            public ulong Size { get; }

            public uint Attributes { get; }

            public DateTime CreationTimeUtc { get; }

            public DateTime LastAccessTimeUtc { get; }

            public DateTime LastWriteTimeUtc { get; }
        }

        private static bool TryGetMeta(string longPath, out EntryMeta meta)
        {
            meta = default;
            try
            {
                if (!NativeMethods.GetFileAttributesExW(
                        longPath,
                        NativeMethods.GetFileExInfoStandard,
                        out NativeMethods.WIN32_FILE_ATTRIBUTE_DATA data))
                {
                    return false;
                }

                bool isDir = (data.dwFileAttributes & NativeMethods.FileAttributeDirectory) != 0;
                meta = new EntryMeta(
                    isDir,
                    isDir ? 0UL : data.Size,
                    data.dwFileAttributes,
                    data.CreationTimeUtc,
                    data.LastAccessTimeUtc,
                    data.LastWriteTimeUtc);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDirectory(string longPath)
            => TryGetMeta(longPath, out EntryMeta meta) && meta.IsDirectory;

        private static bool TryResolveSource(
            OperationSource source,
            out string sourceLong,
            out string sourceDisplay,
            out bool isDirectory,
            out string error)
        {
            sourceLong = string.Empty;
            sourceDisplay = source.SourceDisplayPath;
            isDirectory = false;
            error = string.Empty;

            try
            {
                sourceLong = PathHelper.ToLong(source.SourceDisplayPath);
            }
            catch (Exception ex)
            {
                error = $"源路径不可用：{ex.Message}";
                return false;
            }

            if (!TryGetMeta(sourceLong, out EntryMeta meta))
            {
                error = "源文件已不存在。";
                return false;
            }

            isDirectory = meta.IsDirectory;
            return true;
        }

        private static bool TryResolveTarget(OperationSource source, out string targetLong, out string targetDisplay)
        {
            targetLong = string.Empty;
            targetDisplay = source.TargetDirectoryDisplayPath;

            try
            {
                targetLong = PathHelper.ToLong(source.TargetDirectoryDisplayPath);
                return true;
            }
            catch (Exception ex)
            {
                LogManager.Warning(typeof(OperationEngine), $"目标位置不可用（{source.TargetDirectoryDisplayPath}）：{ex.Message}");
                return false;
            }
        }

        private static bool IsSameOrDescendantPath(string candidateLong, string ancestorLong)
        {
            string candidate = PathHelper.Normalize(candidateLong);
            string ancestor = PathHelper.Normalize(ancestorLong);

            if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string prefix = ancestor.EndsWith('\\') ? ancestor : ancestor + "\\";
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime ToLocal(DateTime utc)
            => utc == DateTime.MinValue ? DateTime.MinValue : utc.ToLocalTime();

        private static void SafeDeleteFile(string longPath)
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
                // 清理临时文件失败不改变结果
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
        }

        private void RecordFailure(
            string name,
            string displayPath,
            string reason,
            int errorCode,
            OperationSource? source)
        {
            var error = new OperationError(
                _kind,
                name,
                displayPath,
                reason,
                errorCode,
                source?.SourceDisplayPath ?? displayPath,
                source?.TargetDirectoryDisplayPath ?? _targetDirectoryDisplayPath);

            _errors.Add(error);

            LogManager.Warning(
                typeof(OperationEngine),
                $"{KindText}失败：{displayPath} — {reason}（错误码 {errorCode}）");
        }

        private static string ReasonFor(int errorCode, string displayPath, bool isTarget)
        {
            switch (errorCode)
            {
                case Win32ErrorMessages.ErrorFileNotFound:
                case Win32ErrorMessages.ErrorPathNotFound:
                    return isTarget ? "目标位置不可用。" : "源文件已不存在。";

                case Win32ErrorMessages.ErrorInvalidDrive:
                case Win32ErrorMessages.ErrorNotReady:
                    return "目标位置不可用（驱动器不存在或设备未就绪）。";

                case Win32ErrorMessages.ErrorAccessDenied:
                    return "没有权限访问此文件或文件夹。";

                case Win32ErrorMessages.ErrorDiskFull:
                    return "磁盘空间不足。";

                case Win32ErrorMessages.ErrorSharingViolation:
                case Win32ErrorMessages.ErrorLockViolation:
                {
                    string? locking = RestartManagerService.DescribeLockingProcesses(displayPath);
                    return locking ?? "文件正被其他程序使用。";
                }

                default:
                    return Win32ErrorMessages.Describe(errorCode);
            }
        }
    }
}
