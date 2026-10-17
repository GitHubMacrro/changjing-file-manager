using System.Runtime.InteropServices;
using CustomWin.Utils.Logs;

namespace ChangJing.App.Services;

/// <summary>Restart Manager：查出「哪个程序正在占用某个文件」，用于重命名 / 删除失败时的中文提示</summary>
// 只读查询，不做任何解锁 / 关进程操作，查询失败一律返回空列表
public static class RestartManagerService
{
    private const int ErrorSuccess = 0;

    // 会话键缓冲区：32 个字符 + 结尾 \0
    private const int CchRmSessionKey = 32 + 1;

    // 最多返回多少个占用者（避免提示框过长）
    private const int MaxReportedProcesses = 5;

    /// <summary>查询正在使用（打开）displayPath 的进程名（如 WINWORD）；查不到时返回空列表，不抛异常</summary>
    public static IReadOnlyList<string> GetLockingProcesses(string displayPath)
    {
        var names = new List<string>();

        if (string.IsNullOrWhiteSpace(displayPath))
        {
            return names;
        }

        // 会话键必须是长度 ≤ 32 的可写缓冲区（函数会回写规范化后的键）
        var sessionKey = new char[CchRmSessionKey];
        string key = Guid.NewGuid().ToString("N");
        key.CopyTo(0, sessionKey, 0, Math.Min(key.Length, CchRmSessionKey - 1));

        uint sessionHandle = 0;

        try
        {
            int result = RmStartSession(out sessionHandle, 0, sessionKey);
            if (result != ErrorSuccess)
            {
                LogManager.Warning(
                    typeof(RestartManagerService),
                    $"Restart Manager 启动会话失败（错误码 {result}），跳过占用者查询。");
                return names;
            }

            string[] resources = { displayPath };
            result = RmRegisterResources(sessionHandle, 1, resources, 0, null, 0, null);
            if (result != ErrorSuccess)
            {
                // 文件不存在 / 路径形式不受支持等都会走到这里，静默回退
                return names;
            }

            uint needed = 0;
            uint arraySize = 0;
            uint rebootReasons = 0;
            result = RmGetList(sessionHandle, out needed, out arraySize, null, ref rebootReasons);
            if (needed == 0)
            {
                return names;
            }

            var processInfo = new RM_PROCESS_INFO[needed];
            arraySize = needed;
            rebootReasons = 0;
            result = RmGetList(sessionHandle, out needed, out arraySize, processInfo, ref rebootReasons);
            if (result != ErrorSuccess)
            {
                return names;
            }

            uint limit = Math.Min(arraySize, (uint)processInfo.Length);
            for (uint i = 0; i < limit; i++)
            {
                string name = (processInfo[i].strAppName ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            if (arraySize > limit)
            {
                names.Add($"…等 {arraySize} 个程序");
            }

            LogManager.Info(
                typeof(RestartManagerService),
                $"Restart Manager 查询到占用程序：{displayPath} → {string.Join("、", names)}");
        }
        catch (Exception ex)
        {
            LogManager.Warning(typeof(RestartManagerService), $"Restart Manager 查询失败：{ex.Message}");
        }
        finally
        {
            if (sessionHandle != 0)
            {
                try
                {
                    RmEndSession(sessionHandle);
                }
                catch
                {
                    // 结束会话失败不影响结果
                }
            }
        }

        return names;
    }

    /// <summary>组合一句面向用户的占用说明；查不到占用者时返回 null</summary>
    public static string? DescribeLockingProcesses(string displayPath)
    {
        IReadOnlyList<string> processes = GetLockingProcesses(displayPath);
        return processes.Count == 0 ? null : $"文件正被 {string.Join("、", processes)} 占用。";
    }

    // 与 Win32 RM_UNIQUE_PROCESS 布局一致
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    // 与 Win32 RM_PROCESS_INFO 布局一致
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmStartSession(
        out uint pSessionHandle,
        int dwSessionFlags,
        [In, Out] char[] strSessionKey);

    [DllImport("rstrtmgr.dll", SetLastError = true)]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        out uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
