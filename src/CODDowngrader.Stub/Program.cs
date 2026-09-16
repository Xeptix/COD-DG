using System.Runtime.InteropServices;

namespace CODDowngrader.Stub;

/// <summary>
/// CODDowngrader.com, the console face of CODDowngrader.exe beside it. Shells wait for a console program and give its exit
/// code, and do neither for a window program, so this runs the window program with this program's own stdin, stdout and
/// stderr, waits for it, and hands back its exit code. PATHEXT puts .COM before .EXE, so typing "CODDowngrader" runs this.
/// </summary>
static unsafe partial class Stub
{
    const int StdInput = -10, StdOutput = -11, StdError = -12;
    const uint StartfUseStdHandles = 0x100, HandleFlagInherit = 1, Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial char* GetCommandLineW();

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    static int Main()
    {
        var exe = Path.ChangeExtension(Environment.ProcessPath!, ".exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"{exe} is missing. Keep CODDowngrader.com and CODDowngrader.exe in the same folder.");
            return 1;
        }

        // Everything after this program's own name, exactly as it was typed, so quoting reaches the window program untouched.
        var raw = new string(GetCommandLineW());
        var rest = raw.StartsWith('"')
            ? raw[(raw.IndexOf('"', 1) is var close and > 0 ? close + 1 : raw.Length)..]
            : raw[(raw.IndexOf(' ') is var space and >= 0 ? space : raw.Length)..];
        var commandLine = $"\"{exe}\"{rest}\0";

        var startupInfo = new StartupInfo { cb = sizeof(StartupInfo), dwFlags = (int)StartfUseStdHandles };
        startupInfo.hStdInput = Inheritable(GetStdHandle(StdInput));
        startupInfo.hStdOutput = Inheritable(GetStdHandle(StdOutput));
        startupInfo.hStdError = Inheritable(GetStdHandle(StdError));

        // Ctrl+C reaches the window program as well, through the console it attaches to, and it decides what stopping means.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        fixed (char* line = commandLine)
        {
            if (!CreateProcessW(null, line, 0, 0, true, 0, 0, null, ref startupInfo, out var process))
            {
                Console.Error.WriteLine($"{exe} could not be started (error {Marshal.GetLastPInvokeError()}).");
                return 1;
            }
            WaitForSingleObject(process.hProcess, Infinite);
            return GetExitCodeProcess(process.hProcess, out var code) ? (int)code : 1;
        }
    }

    static nint Inheritable(nint handle)
    {
        if (handle != 0 && handle != -1) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
        return handle;
    }
}
