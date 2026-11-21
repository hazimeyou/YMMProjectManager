using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Keeps the helper inside a kill-on-close Windows job from its first instruction.</summary>
internal sealed class GitHubLoginProcess : IDisposable
{
    private static readonly IntPtr HandleList = new(0x00020002);
    private static readonly IntPtr JobList = new(0x0002000D);
    private readonly SafeFileHandle job;
    private readonly SafeFileHandle process;
    private readonly ProcessWaitHandle wait;
    private readonly RegisteredWaitHandle registration;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GitHubLoginProcess(SafeFileHandle job, SafeFileHandle process)
    {
        this.job = job;
        this.process = process;
        wait = new ProcessWaitHandle(process);
        registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => completion.TrySetResult(),
            null, Timeout.Infinite, executeOnlyOnce: true);
    }

    internal Task Exited => completion.Task;
    internal bool HasExited => wait.WaitOne(0);
    internal void Terminate()
    {
        if (!TerminateJobObject(job, 1)) job.Dispose();
    }

    internal int ReadExitCode()
    {
        if (!GetExitCodeProcess(process, out var code))
            throw new Win32Exception();
        return unchecked((int)code);
    }

    internal static GitHubLoginProcess Start(string executable, string arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        SafeFileHandle? process = null;
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new JobLimits { Basic = new BasicJobLimits { Flags = 0x2000 | 0x1000 } };
            // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK. The reviewed GCM performs
            // the OAuth HTTP itself. OS browser activation is allowed to outlive it.
            if (!SetInformationJobObject(job, 9, in limits, Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception();

            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            using var silence = CreateFile(@"\\?\GLOBALROOT\Device\Null", 0xC0000000, 0x03, in security, 3, 0, IntPtr.Zero);
            if (silence.IsInvalid) throw new Win32Exception();
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception();
            var attributes = Marshal.AllocHGlobal(size);
            var initialized = false;
            var stdio = Marshal.AllocHGlobal(IntPtr.Size);
            var jobs = Marshal.AllocHGlobal(IntPtr.Size);
            var block = IntPtr.Zero;
            try
            {
                if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception();
                initialized = true;
                Marshal.WriteIntPtr(stdio, silence.DangerousGetHandle());
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, HandleList, stdio, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                    !UpdateProcThreadAttribute(attributes, 0, JobList, jobs, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();

                block = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => $"{entry.Key}={entry.Value}")) + '\0');
                var startup = new StartupInfoEx
                {
                    Attributes = attributes,
                    Startup = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = 0x100, // STARTF_USESTDHANDLES
                        StandardInput = silence.DangerousGetHandle(),
                        StandardOutput = silence.DangerousGetHandle(),
                        StandardError = silence.DangerousGetHandle(),
                    },
                };
                var command = new StringBuilder($"\"{executable}\" {arguments}");
                const uint flags = 0x00080000 | 0x08000000 | 0x00000400; // EXTENDED_STARTUPINFO_PRESENT, CREATE_NO_WINDOW, CREATE_UNICODE_ENVIRONMENT
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, flags, block, workingDirectory, in startup, out var child))
                    throw new Win32Exception();
                process = new SafeFileHandle(child.Process, ownsHandle: true);
                using var thread = new SafeFileHandle(child.Thread, ownsHandle: true);
                GitHubLoginProcess operation;
                try { operation = new GitHubLoginProcess(job, process); }
                catch
                {
                    // Closing the job terminates the contained process. Wait on its
                    // native handle even if registration of the async wait failed.
                    job.Dispose();
                    WaitForSingleObject(process, uint.MaxValue);
                    throw;
                }
                job = null!;
                process = null;
                return operation;
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(block);
                Marshal.FreeHGlobal(jobs);
                Marshal.FreeHGlobal(stdio);
                Marshal.FreeHGlobal(attributes);
            }
        }
        finally
        {
            job?.Dispose(); // If startup fails after creation, close the job before releasing the process handle.
            process?.Dispose();
        }
    }

    public void Dispose()
    {
        registration.Unregister(null);
        job.Dispose();
        wait.Dispose();
        process.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeFileHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharsX, CharsY, FillAttributes, Flags;
        public ushort ShowWindow, ReservedCount;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicJobLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobLimits limits, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, in SecurityAttributes security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int capacity, uint flags, ref IntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string directory, in StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
}
