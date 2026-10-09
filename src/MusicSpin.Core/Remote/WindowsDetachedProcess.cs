using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MusicSpin.Core.Remote;

/// <summary>
/// <c>CreateProcessW</c> for the detached background player on Windows. <see cref="System.Diagnostics.Process"/> cannot do
/// this: with <c>UseShellExecute = false</c> it always passes <c>bInheritHandles = TRUE</c>, so the child inherits every
/// inheritable handle of ours, including a caller's redirected stdout/stderr pipes (an agent or test harness reading our
/// output would then wait for EOF until the player exits). Here the child gets exactly the three standard handles we pass
/// (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>) and nothing else.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsDetachedProcess
{
    // Creation flags. CREATE_NO_WINDOW: a private, hidden console (Ctrl+C or closing the caller's console never reaches
    // the child, but console APIs and console control events such as CTRL_SHUTDOWN still work, unlike DETACHED_PROCESS).
    public const uint CreateNoWindow = 0x08000000;
    public const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const nint ProcThreadAttributeHandleList = 0x00020002;
    private const int ErrorAccessDenied = 5;

    private const uint GenericRead = 0x80000000;
    private const uint FileAppendData = 0x00000004;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>
    /// Starts <paramref name="exe"/> with only the given standard handles inherited and returns its process id. Tries to
    /// break away from the caller's job object first (so closing that job does not kill the child), and starts it
    /// normally when the job does not allow breaking away.
    /// </summary>
    public static int Start(string exe, IEnumerable<string> arguments, string? workingDirectory,
        SafeHandle stdIn, SafeHandle stdOut, SafeHandle stdErr, uint creationFlags)
    {
        try
        {
            return StartCore(exe, arguments, workingDirectory, stdIn, stdOut, stdErr, creationFlags | CreateBreakawayFromJob);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            // In a job without JOB_OBJECT_LIMIT_BREAKAWAY_OK: stay in it.
            return StartCore(exe, arguments, workingDirectory, stdIn, stdOut, stdErr, creationFlags);
        }
    }

    private static int StartCore(string exe, IEnumerable<string> arguments, string? workingDirectory,
        SafeHandle stdIn, SafeHandle stdOut, SafeHandle stdErr, uint creationFlags)
    {
        var refs = new[] { stdIn, stdOut, stdErr };
        var added = new bool[refs.Length];
        try
        {
            for (var i = 0; i < refs.Length; i++)
            {
                refs[i].DangerousAddRef(ref added[i]);
            }

            var handles = refs.Select(h => h.DangerousGetHandle()).Distinct().ToArray();
            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size); // asks for the size
            var list = Marshal.AllocHGlobal(size);
            try
            {
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");
                }

                try
                {
                    var commandLine = (WindowsCommandLine.Build(exe, arguments) + '\0').ToCharArray(); // CreateProcessW may write to it
                    fixed (nint* handleList = handles)
                    fixed (char* cmd = commandLine)
                    {
                        if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handleList, (nint)(handles.Length * sizeof(nint)), 0, 0))
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed");
                        }

                        var startup = new StartupInfoEx
                        {
                            Cb = (uint)sizeof(StartupInfoEx),
                            Flags = StartfUseStdHandles,
                            StdInput = stdIn.DangerousGetHandle(),
                            StdOutput = stdOut.DangerousGetHandle(),
                            StdError = stdErr.DangerousGetHandle(),
                            AttributeList = list,
                        };
                        ProcessInformation info;
                        if (!CreateProcess(exe, cmd, 0, 0, true, creationFlags | ExtendedStartupInfoPresent, 0, workingDirectory, &startup, &info))
                        {
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcess failed for '{exe}'");
                        }

                        _ = CloseHandle(info.Thread);
                        _ = CloseHandle(info.Process);
                        return (int)info.ProcessId;
                    }
                }
                finally
                {
                    DeleteProcThreadAttributeList(list);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }
        }
        finally
        {
            for (var i = 0; i < refs.Length; i++)
            {
                if (added[i])
                {
                    refs[i].DangerousRelease();
                }
            }
        }
    }

    /// <summary>An inheritable read handle on <c>NUL</c> (stdin of a detached process).</summary>
    public static SafeFileHandle OpenNullInput() => Open("NUL", GenericRead, OpenExisting);

    /// <summary>An inheritable append-only handle on <paramref name="path"/> (every write goes to the end, like <c>&gt;&gt;</c>).</summary>
    public static SafeFileHandle OpenAppend(string path) => Open(path, FileAppendData | Synchronize, OpenAlways);

    private static SafeFileHandle Open(string path, uint access, uint disposition)
    {
        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        var handle = CreateFile(path, access, FileShareAll, &security, disposition, FileAttributeNormal, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"Cannot open '{path}': {new System.ComponentModel.Win32Exception(error).Message}", error);
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory,
        StartupInfoEx* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, SecurityAttributes* securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, uint flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nint attribute, void* value, nint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>Windows command-line quoting (the rules of <c>CommandLineToArgvW</c> and the C runtime, as .NET uses them).</summary>
internal static class WindowsCommandLine
{
    /// <summary>A command line that the C runtime (and .NET) parse back into exactly these arguments.</summary>
    public static string Build(string exe, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        Append(sb, exe);
        foreach (var argument in arguments)
        {
            Append(sb, argument);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string argument)
    {
        if (sb.Length != 0)
        {
            sb.Append(' ');
        }

        if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    i++;
                    backslashes++;
                }

                if (i == argument.Length)
                {
                    sb.Append('\\', backslashes * 2); // before the closing quote
                }
                else if (argument[i] == '"')
                {
                    sb.Append('\\', (backslashes * 2) + 1).Append('"');
                    i++;
                }
                else
                {
                    sb.Append('\\', backslashes);
                }

                continue;
            }

            if (c == '"')
            {
                sb.Append('\\').Append('"');
                continue;
            }

            sb.Append(c);
        }

        sb.Append('"');
    }
}
