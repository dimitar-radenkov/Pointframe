#Requires -Version 7.0
function Initialize-ProcessJob
{
    # A Windows job object with kill-on-close holds the whole process tree of a launched executor: terminating the job
    # ends children and grandchildren, closing the last handle (even when the owner crashes) ends them too, and the
    # active-process count says whether the tree is really gone. The executor is created suspended and assigned to the
    # job before its first instruction runs, so it cannot spawn a child outside the job.
    if ('AwProcessJob' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AwProcessJob
{
    [StructLayout(LayoutKind.Sequential)] struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct BASIC
    {
        public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED
    {
        public BASIC Basic; public IO_COUNTERS Io; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] struct ACCOUNTING
    {
        public long a, b, c, d; public uint TotalPageFaultCount; public uint TotalProcesses; public uint ActiveProcesses; public uint TotalTerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES attributes, uint disposition, uint flags, IntPtr template);

    public static IntPtr Create()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { return IntPtr.Zero; }
        EXTENDED info = new EXTENDED();
        info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int length = Marshal.SizeOf(typeof(EXTENDED));
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, 9, buffer, (uint)length)) { CloseHandle(job); return IntPtr.Zero; }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return job;
    }
    public static bool Kill(IntPtr job) { return TerminateJobObject(job, 1); }
    public static void Close(IntPtr job) { CloseHandle(job); }
    public static int Active(IntPtr job)
    {
        int length = Marshal.SizeOf(typeof(ACCOUNTING));
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(job, 1, buffer, (uint)length, IntPtr.Zero)) { return -1; }
            return (int)((ACCOUNTING)Marshal.PtrToStructure(buffer, typeof(ACCOUNTING))).ActiveProcesses;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static IntPtr OpenInheritable(string path, bool write)
    {
        SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
        sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
        sa.bInheritHandle = 1;
        return CreateFileW(path, write ? 0x40000000u : 0x80000000u, 3u, ref sa, write ? 2u : 3u, 0u, IntPtr.Zero);
    }

    // Creates the process suspended with redirected standard streams and assigns it to the job. It does not run until
    // Resume. Returns false (and kills the process if it was created) when it cannot be created or contained.
    public static bool StartSuspendedInJob(IntPtr job, string commandLine, string directory, string stdin, string stdout, string stderr, out int processId, out IntPtr process, out IntPtr thread, out string error)
    {
        processId = 0; process = IntPtr.Zero; thread = IntPtr.Zero; error = null;
        IntPtr hIn = OpenInheritable(string.IsNullOrEmpty(stdin) ? "NUL" : stdin, false);
        IntPtr hOut = OpenInheritable(stdout, true);
        IntPtr hErr = OpenInheritable(stderr, true);
        try
        {
            if (hIn == new IntPtr(-1) || hOut == new IntPtr(-1) || hErr == new IntPtr(-1)) { error = "cannot open the redirected streams (error " + Marshal.GetLastWin32Error() + ")"; return false; }
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = 0x100; // STARTF_USESTDHANDLES
            si.hStdInput = hIn; si.hStdOutput = hOut; si.hStdError = hErr;
            PROCESS_INFORMATION pi;
            // CREATE_SUSPENDED | CREATE_NO_WINDOW
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, 0x4u | 0x08000000u, IntPtr.Zero, directory, ref si, out pi))
            {
                error = "CreateProcess failed (error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            if (!AssignProcessToJobObject(job, pi.hProcess))
            {
                error = "cannot assign the process to the job object (error " + Marshal.GetLastWin32Error() + ")";
                TerminateProcess(pi.hProcess, 1);
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                return false;
            }
            processId = pi.dwProcessId;
            process = pi.hProcess;
            thread = pi.hThread;
            return true;
        }
        finally
        {
            if (hIn != IntPtr.Zero && hIn != new IntPtr(-1)) { CloseHandle(hIn); }
            if (hOut != IntPtr.Zero && hOut != new IntPtr(-1)) { CloseHandle(hOut); }
            if (hErr != IntPtr.Zero && hErr != new IntPtr(-1)) { CloseHandle(hErr); }
        }
    }
    public static bool Wait(IntPtr process, long milliseconds)
    {
        uint wait = milliseconds >= uint.MaxValue ? 0xFFFFFFFEu : (uint)milliseconds;
        return WaitForSingleObject(process, wait) == 0;
    }
    public static long ExitCode(IntPtr process)
    {
        uint code;
        return GetExitCodeProcess(process, out code) ? (long)(int)code : long.MinValue;
    }
    public static void CloseProcess(IntPtr process) { CloseHandle(process); }
    public static bool Resume(IntPtr thread)
    {
        bool ok = ResumeThread(thread) != 0xFFFFFFFFu;
        CloseHandle(thread);
        return ok;
    }
}
'@
}

function Start-ProcessInJob([IntPtr]$Job, [string]$FilePath, [string[]]$ArgumentList, [string]$WorkingDirectory, [string]$StdinPath, [string]$StdoutPath, [string]$StderrPath)
{
    # Creates the process suspended, puts it into the job, then lets it run. Known limitation (accepted): if the calling
    # script itself dies between creating the process and assigning it, that one suspended process is not contained
    # (it never ran, so it has no children; it stays suspended).
    Initialize-ProcessJob
    $commandLine = '"' + $FilePath + '"' + (@($ArgumentList | ForEach-Object { ' ' + $_ }) -join '')
    $processId = 0
    $thread = [IntPtr]::Zero
    $handle = [IntPtr]::Zero
    $problem = $null
    if (-not [AwProcessJob]::StartSuspendedInJob($Job, $commandLine, $WorkingDirectory, $StdinPath, $StdoutPath, $StderrPath, [ref]$processId, [ref]$handle, [ref]$thread, [ref]$problem))
    {
        throw "cannot start the executor inside its job object: $problem"
    }
    if (-not [AwProcessJob]::Resume($thread)) { [AwProcessJob]::Kill($Job) | Out-Null; throw 'cannot resume the executor process' }
    # Own handle: Process.ExitCode of a process opened by id is unreliable from PowerShell, a real handle is not.
    [pscustomobject]@{ Id = $processId; Handle = $handle }
}

function Wait-ProcessExit($Process, [long]$Milliseconds) { [AwProcessJob]::Wait($Process.Handle, $Milliseconds) }

function Test-ProcessExited($Process) { [AwProcessJob]::Wait($Process.Handle, 0) }

function Get-ProcessExitCode($Process)
{
    $code = [AwProcessJob]::ExitCode($Process.Handle)
    if ($code -eq [long]::MinValue) { return $null }
    [int]$code
}

function Stop-ProcessJob([IntPtr]$Job, [int]$WaitSeconds = 10)
{
    # Terminates every process in the job and waits, bounded, until none is left. True only when the tree is gone.
    [AwProcessJob]::Kill($Job) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do
    {
        if ([AwProcessJob]::Active($Job) -eq 0) { return $true }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $false
}

function Test-JobContainment([IntPtr]$Job)
{
    # After a normal exit: is the executor's tree provably gone? Leftover processes are killed and verified; a job that
    # cannot be queried (Active() = -1) or cleaned is reported as unverified, never as clean.
    $active = [AwProcessJob]::Active($Job)
    if ($active -lt 0) { return [pscustomobject]@{ Verified = $false; Alive = $false; Leftover = 0; Reason = 'the job object could not be queried' } }
    if ($active -eq 0) { return [pscustomobject]@{ Verified = $true; Alive = $false; Leftover = 0; Reason = $null } }
    $gone = Stop-ProcessJob $Job 10
    [pscustomobject]@{ Verified = $gone; Alive = (-not $gone); Leftover = $active; Reason =$(if ($gone) { $null } else { "$active leftover process(es) could not be terminated" }) }
}

function Get-ProcessStartIdentity([int]$ProcessId)
{
    try { [Diagnostics.Process]::GetProcessById($ProcessId).StartTime.ToUniversalTime().ToString('o') } catch { $null }
}

function Test-ProcessIdentityAlive([int]$ProcessId, [string]$StartUtc)
{
    # The id alone is not an identity (ids are reused): the recorded start time must match too.
    if (-not $ProcessId -or -not $StartUtc) { return $false }
    (Get-ProcessStartIdentity $ProcessId) -eq $StartUtc
}

function Get-ProcessTreeSnapshot([int]$RootPid)
{
    # The root and every descendant alive now, each with its creation time so that a reused id is not mistaken for it.
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate -ErrorAction Stop)
    $byParent = @{}
    foreach ($p in $all) { if (-not $byParent.ContainsKey([int]$p.ParentProcessId)) { $byParent[[int]$p.ParentProcessId] = @() }; $byParent[[int]$p.ParentProcessId] += @($p) }
    $found = [Collections.Generic.List[object]]::new()
    $queue = [Collections.Generic.Queue[int]]::new()
    $root = $all | Where-Object { [int]$_.ProcessId -eq $RootPid } | Select-Object -First 1
    if ($root) { $found.Add([pscustomobject]@{ Id = $RootPid; Created = $root.CreationDate }); $queue.Enqueue($RootPid) }
    while ($queue.Count)
    {
        $parent = $queue.Dequeue()
        foreach ($child in @($byParent[$parent])) { if ($child -and [int]$child.ProcessId -ne $parent) { $found.Add([pscustomobject]@{ Id = [int]$child.ProcessId; Created = $child.CreationDate }); $queue.Enqueue([int]$child.ProcessId) } }
    }
    @($found)
}

function Test-SnapshotProcessAlive($Entry)
{
    $live = Get-Process -Id $Entry.Id -ErrorAction SilentlyContinue
    if (-not $live) { return $false }
    try { [Math]::Abs(($live.StartTime.ToUniversalTime() - ([datetime]$Entry.Created).ToUniversalTime()).TotalSeconds) -lt 2 } catch { $true }
}

function Stop-ProcessTreeById([int]$ProcessId, [int]$WaitSeconds = 10)
{
    # Fallback without a job object (a surviving executor found by recovery). True only when the root and every
    # descendant recorded at kill time are gone (taskkill's own exit code is not trusted over that check). Descendants
    # whose parent already died cannot be found this way.
    try { $targets = @(Get-ProcessTreeSnapshot $ProcessId) } catch { return $false }
    if ($targets.Count -eq 0) { return $true }
    & taskkill.exe /PID $ProcessId /T /F 2>&1 | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do
    {
        $alive = @($targets | Where-Object { Test-SnapshotProcessAlive $_ })
        if ($alive.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $false
}
