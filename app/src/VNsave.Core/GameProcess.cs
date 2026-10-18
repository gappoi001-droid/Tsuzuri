using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VNsave.Core;

public interface IGameProcess
{
    bool IsRunning(string executable);
    Task RunAsync(LocalGame game, CancellationToken ct);
}

public sealed class GameProcess : IGameProcess
{
    public bool IsRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                try
                {
                    using var handle = OpenProcess(0x1000, false, process.Id);
                    if (handle.IsInvalid) { if (!process.HasExited) return true; continue; }
                    var path = new StringBuilder(32768);
                    uint size = (uint)path.Capacity;
                    if (!QueryFullProcessImageName(handle, 0, path, ref size)) return true;
                    if (Path.GetFullPath(path.ToString()).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { return true; }
            }
        }
        return false;
    }
    public async Task RunAsync(LocalGame game, CancellationToken ct)
    {
        GameDetector.ValidateExecutable(game.ExecutablePath);
        if (IsRunning(game.ExecutablePath)) throw new UserError("このゲームは既に起動しています。");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        string command = "\"" + game.ExecutablePath + "\"" + (game.Arguments.Length == 0 ? "" : " " + game.Arguments);
        if (!CreateProcess(game.ExecutablePath, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x4,
            IntPtr.Zero, game.WorkingDirectory, ref startup, out var info))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 740) throw new Win32Exception(error);
            using var elevated = Process.Start(new ProcessStartInfo(game.ExecutablePath, game.Arguments)
                { UseShellExecute = true, Verb = "runas", WorkingDirectory = game.WorkingDirectory }) ?? throw new UserError("昇格したゲームを起動できませんでした。");
            await elevated.WaitForExitAsync(ct);
            return;
        }
        using var process = new SafeFileHandle(info.Process, true);
        using var thread = new SafeFileHandle(info.Thread, true);
        using var job = CreateJobObject(IntPtr.Zero, null);
        bool resumed = false;
        try
        {
            if (job.IsInvalid || !AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            resumed = true;
            while (true)
            {
                if (!QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (accounting.ActiveProcesses == 0) return;
                await Task.Delay(1000, ct);
            }
        }
        finally
        {
            // Only an unstarted suspended process is terminated on launch failure.
            // Closing a job never kills an already running game: KILL_ON_JOB_CLOSE is not set.
            if (!resumed) TerminateProcess(process, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr Reserved2, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobAccounting
    { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string application, StringBuilder command,
        IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobAccounting info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder path, ref uint size);
}

public static class SaveSettler
{
    public static async Task WaitAsync(SaveLocation[] locations, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var wait = delay ?? Task.Delay;
        string? previous = null;
        int stable = 0;
        for (int second = 0; second < 30; second++)
        {
            await wait(TimeSpan.FromSeconds(1), ct);
            string current = string.Join('\n', locations.SelectMany(SaveFiles.Enumerate).Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => { var info = new FileInfo(p); return p + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks; }));
            stable = current == previous ? stable + 1 : 1;
            if (stable >= 3) return;
            previous = current;
        }
        throw new UserError("終了後 30 秒待ってもセーブの書き込みが落ち着きませんでした。");
    }
}
