using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileUnlocker;

internal static unsafe partial class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!IsUserAnAdmin())
        {
            var exe = Environment.ProcessPath ?? "";
            var arg = args.Length > 0 ? $"\"{args[0]}\"" : "";
            ShellExecute(IntPtr.Zero, "runas", exe, arg, "", 1);
            return;
        }

        if (args.Length == 0)
            ToggleContextMenu();
        else if (File.Exists(args[0]) || Directory.Exists(args[0])) UnlockPath(args[0]);
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsUserAnAdmin();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOp, string lpFile, string lpArgs, string lpDir,
        int nShow);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int dwAccess, bool bInherit, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char* strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApps,
        IntPtr rgApps, uint nServices, IntPtr rgsServices);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public int dwProcessId;
        public long ProcessStartTime;
        public fixed char strAppName[256];
        public fixed char strServiceShortName[64];
        public int ApplicationType;
        public int AppStatus;
        public int TSSessionId;
        public int bRestartable;
    }

    private static void UnlockPath(string path)
    {
        var filesToUnlock = new List<string>();

        if (File.Exists(path))
        {
            filesToUnlock.Add(path);
        }
        else if (Directory.Exists(path))
        {
            filesToUnlock.Add(path);

            try
            {
                filesToUnlock.AddRange(Directory.GetFiles(path, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true
                }));
            }
            catch
            {
                // 忽略异常，继续执行
            }
        }

        if (filesToUnlock.Count == 0) return;

        var sessionKey = stackalloc char[33];
        if (RmStartSession(out var handle, 0, sessionKey) != 0) return;

        try
        {
            var rgsFilenames = filesToUnlock.ToArray();

            if (RmRegisterResources(handle, (uint)rgsFilenames.Length, rgsFilenames, 0, IntPtr.Zero, 0, IntPtr.Zero) !=
                0) return;

            uint arraySize = 0;
            RmGetList(handle, out var needed, ref arraySize, null, out _);

            if (needed <= 0) return;
            var processInfo = new RmProcessInfo[needed];
            if (RmGetList(handle, out needed, ref needed, processInfo, out _) != 0) return;

            var myPid = Environment.ProcessId;
            foreach (var info in processInfo)
            {
                if (info.dwProcessId == myPid) continue;
                var hProcess = OpenProcess(0x0001 /* PROCESS_TERMINATE */, false, info.dwProcessId);
                if (hProcess == IntPtr.Zero) continue;
                TerminateProcess(hProcess, 0);
                CloseHandle(hProcess);
            }
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    private static void ToggleContextMenu()
    {
        var exe = Environment.ProcessPath ?? "";
        const string key = @"*\shell\UnlockFile";
        const string dirKey = @"Directory\shell\UnlockDir";

        try
        {
            if (Registry.ClassesRoot.OpenSubKey(key) != null)
            {
                Registry.ClassesRoot.DeleteSubKeyTree(key, false);
                Registry.ClassesRoot.DeleteSubKeyTree(dirKey, false);
                MessageBox(IntPtr.Zero, "卸载成功！", "解除占用", 0x40);
            }
            else
            {
                void CreateMenu(string path)
                {
                    using var k = Registry.ClassesRoot.CreateSubKey(path);
                    k.SetValue("", "解除占用");
                    k.SetValue("Extended", "解除占用");
                    using var cmd = k.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" \"%1\"");
                }

                CreateMenu(key);
                CreateMenu(dirKey);
                MessageBox(IntPtr.Zero, "安装成功！Shift+右键使用了。", "解除占用", 0x40);
            }
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, $"操作失败: {ex.Message}", "错误", 0x10);
        }
    }
}