using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YMMProjectManager.Infrastructure.Git;

/// <summary>Keeps the helper inside a kill-on-close Windows job from its first instruction.</summary>
//CSJ,class=GitHubLoginProcess,GitHub認証補助プロセス
internal sealed class GitHubLoginProcess : IDisposable
{
    //CSJ,field=HandleList,ハンドル一覧
    private static readonly IntPtr HandleList = new(0x00020002);
    //CSJ,field=JobList,ジョブ一覧
    private static readonly IntPtr JobList = new(0x0002000D);
    //CSJ,field=job,ジョブ
    private readonly SafeFileHandle job;
    //CSJ,field=process,プロセス
    private readonly SafeFileHandle process;
    //CSJ,field=wait,待機
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

    //CSJ,method=ReadExitCode,終了コードを読み取る
    internal int ReadExitCode()
    {
        if (!GetExitCodeProcess(process, out var code))
            throw new Win32Exception();
        return unchecked((int)code);
    }

    //CSJ,method=Start,開始する
    internal static GitHubLoginProcess Start(string executable, string arguments, string workingDirectory,
        //CSJ,parameter=environment,環境
        IReadOnlyDictionary<string, string> environment)
    {
        //CSJ,variable=job,ジョブ
        var job = CreateJobObject(IntPtr.Zero, null);
        //CSJ,variable=process,プロセス
        SafeFileHandle? process = null;
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            //CSJ,variable=limits,上限
            var limits = new JobLimits { Basic = new BasicJobLimits { Flags = 0x2000 | 0x1000 } };
            // KILL_ON_JOB_CLOSE | SILENT_BREAKAWAY_OK. The reviewed GCM performs
            // the OAuth HTTP itself. OS browser activation is allowed to outlive it.
            if (!SetInformationJobObject(job, 9, in limits, Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception();

            //CSJ,variable=security,セキュリティ
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            using var silence = CreateFile(@"\\?\GLOBALROOT\Device\Null", 0xC0000000, 0x03, in security, 3, 0, IntPtr.Zero);
            if (silence.IsInvalid) throw new Win32Exception();
            //CSJ,variable=size,サイズ
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == IntPtr.Zero) throw new Win32Exception();
            //CSJ,variable=attributes,属性
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
                //CSJ,variable=startup,起動
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
                //CSJ,variable=command,コマンド
                var command = new StringBuilder($"\"{executable}\" {arguments}");
                //CSJ,variable=flags,フラグ
                const uint flags = 0x00080000 | 0x08000000 | 0x00000400; // EXTENDED_STARTUPINFO_PRESENT, CREATE_NO_WINDOW, CREATE_UNICODE_ENVIRONMENT
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, flags, block, workingDirectory, in startup, out var child))
                    throw new Win32Exception();
                process = new SafeFileHandle(child.Process, ownsHandle: true);
                //CSJ,variable=thread,スレッド
                using var thread = new SafeFileHandle(child.Thread, ownsHandle: true);
                //CSJ,variable=operation,操作
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

    //CSJ,method=Dispose,破棄する
    public void Dispose()
    {
        registration.Unregister(null);
        job.Dispose();
        wait.Dispose();
        process.Dispose();
    }

    //CSJ,class=ProcessWaitHandle,プロセス待機ハンドル
    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeFileHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    //CSJ,struct=SecurityAttributes,セキュリティ属性
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        //CSJ,field=Length,長さ
        public int Length;
        //CSJ,field=Descriptor,記述情報
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    //CSJ,struct=StartupInfo,起動情報
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        //CSJ,field=Size,サイズ
        public int Size;
        //CSJ,field=Reserved,予約済み
        public IntPtr Reserved, Desktop, Title;
        //CSJ,field=Width,幅
        public uint X, Y, Width, Height, CharsX, CharsY, FillAttributes, Flags;
        //CSJ,field=ShowWindow,表示ウィンドウ
        public ushort ShowWindow, ReservedCount;
        //CSJ,field=ReservedData,予約済みデータ
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    //CSJ,struct=StartupInfoEx,拡張プロセス起動情報
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        //CSJ,field=Startup,起動
        public StartupInfo Startup;
        //CSJ,field=Attributes,属性
        public IntPtr Attributes;
    }

    //CSJ,struct=ProcessInformation,プロセス情報
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        //CSJ,field=Process,プロセス
        public IntPtr Process, Thread;
        //CSJ,field=ProcessId,プロセスID
        public uint ProcessId, ThreadId;
    }

    //CSJ,struct=BasicJobLimits,基本ジョブ上限
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long PerProcessTime, PerJobTime;
        //CSJ,field=Flags,フラグ
        public uint Flags;
        //CSJ,field=MinimumWorkingSet,最小作業中設定
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        //CSJ,field=Priority,優先順位
        public uint Priority, Scheduling;
    }

    //CSJ,struct=JobLimits,ジョブ上限
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        //CSJ,field=Basic,基本
        public BasicJobLimits Basic;
        //CSJ,field=ReadBytes,読み取りバイト列
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        //CSJ,field=ProcessMemoryLimit,プロセスメモリ上限
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    //CSJ,method=CreateJobObject,ジョブオブジェクトを作成する
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr security, string? name);

    //CSJ,method=SetInformationJobObject,情報ジョブオブジェクトを設定する
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobLimits limits, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    //CSJ,method=CreateFile,ファイルを作成する
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, in SecurityAttributes security,
        //CSJ,parameter=disposition,処置区分
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int capacity, uint flags, ref IntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value,
        //CSJ,parameter=size,サイズ
        IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

    //CSJ,method=CreateProcess,プロセスを作成する
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        //CSJ,parameter=threadAttributes,スレッド属性
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        //CSJ,parameter=directory,ディレクトリ
        string directory, in StartupInfoEx startup, out ProcessInformation process);

    //CSJ,method=GetExitCodeProcess,終了コードプロセスを取得する
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle process, uint milliseconds);
}
