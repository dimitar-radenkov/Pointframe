#Requires -Version 7.0
function Initialize-ProcessJob
{
    # A Windows job object with kill-on-close holds the whole process tree of a launched executor: terminating the job
    # ends children and grandchildren, closing the last handle (even when the owner crashes) ends them too, and the
    # active-process count says whether the tree is really gone. The executor is created suspended and assigned to the
    # job before its first instruction runs, so it cannot spawn a child outside the job.
    if ('AwProcessJob' -as [type])
    {
        # A type loaded earlier in this process cannot be replaced; a copy without KillIfCreated is stale.
        if (-not ([AwProcessJob].GetMethod('KillIfCreated'))) { throw 'AwProcessJob was loaded from an older process-tree.ps1; start a new pwsh session.' }
        return
    }
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
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
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
        // The same path for both streams is one handle (like 2>&1): the two streams interleave in arrival order.
        bool sameStream = string.Equals(stdout, stderr, StringComparison.OrdinalIgnoreCase);
        IntPtr hErr = sameStream ? hOut : OpenInheritable(stderr, true);
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
            if (!sameStream && hErr != IntPtr.Zero && hErr != new IntPtr(-1)) { CloseHandle(hErr); }
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
    // Identity check and termination on one handle, so a reused id can never be hit between the two. The expected creation
    // time is a FILETIME in whole microseconds (a multiple of 10 ticks, CIM's precision); the process's own is truncated the
    // same way and must be equal.
    // 0 = gone (not running, or terminated and confirmed); 1 = unverified (access denied on a live process, a read failed):
    // never killed; 2 = terminated but not yet gone within the wait (retry); 3 = a different process holds the id, so the
    // target is gone: the holder is never touched.
    public static int KillIfCreated(int processId, long expectedCreatedFileTime, long waitMilliseconds)
    {
        IntPtr h = OpenProcess(0x0001 | 0x1000 | 0x00100000, false, processId); // TERMINATE | QUERY_LIMITED_INFORMATION | SYNCHRONIZE
        if (h == IntPtr.Zero) { return Marshal.GetLastWin32Error() == 87 ? 0 : 1; } // ERROR_INVALID_PARAMETER: no such process
        try
        {
            long created, exited, kernel, user;
            if (!GetProcessTimes(h, out created, out exited, out kernel, out user)) { return 1; }
            if (created - (created % 10) != expectedCreatedFileTime) { return 3; }
            if (WaitForSingleObject(h, 0) != 0)
            {
                if (!TerminateProcess(h, 1) && WaitForSingleObject(h, 0) != 0) { return 1; }
            }
            uint wait = waitMilliseconds >= uint.MaxValue ? 0xFFFFFFFEu : (uint)(waitMilliseconds < 0 ? 0 : waitMilliseconds);
            return WaitForSingleObject(h, wait) == 0 ? 0 : 2;
        }
        finally { CloseHandle(h); }
    }
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

function Resolve-ProcessTreeEx($Processes, [int]$RootPid)
{
    # Pure: the root and its descendants from a process list (Id, ParentId, Created). Windows never clears a recorded
    # parent id when the parent exits, so an id reused by a newer process makes older, unrelated processes look like its
    # children. A real child is never older than its parent, so an entry counts only when its Created is not earlier
    # than its parent's, at every level. This narrows adoption; it cannot prove ancestry (an equal or newer time proves
    # nothing). Incomplete is set when a process that names a target as its parent has no readable creation time (or the
    # target has none): such a process can be neither adopted nor ignored safely.
    $all = @($Processes)
    $byParent = @{}
    foreach ($p in $all) { $k = [int]$p.ParentId; if (-not $byParent.ContainsKey($k)) { $byParent[$k] = [Collections.Generic.List[object]]::new() }; $byParent[$k].Add($p) }
    $targets = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[int]]::new()
    $queue = [Collections.Generic.Queue[object]]::new()
    $incomplete = $false
    $root = $all | Where-Object { [int]$_.Id -eq $RootPid } | Select-Object -First 1
    if ($root)
    {
        $entry = [pscustomobject]@{ Id = $RootPid; Created = $root.Created }
        $targets.Add($entry); [void]$seen.Add($RootPid); $queue.Enqueue($entry)
    }
    while ($queue.Count)
    {
        $parent = $queue.Dequeue()
        if (-not $byParent.ContainsKey([int]$parent.Id)) { continue }
        foreach ($child in $byParent[[int]$parent.Id])
        {
            $id = [int]$child.Id
            if ($seen.Contains($id)) { continue }
            if ($null -eq $parent.Created -or $null -eq $child.Created) { $incomplete = $true; continue }
            if (([datetime]$child.Created).ToUniversalTime() -lt ([datetime]$parent.Created).ToUniversalTime()) { continue }
            [void]$seen.Add($id)
            $entry = [pscustomobject]@{ Id = $id; Created = $child.Created }
            $targets.Add($entry); $queue.Enqueue($entry)
        }
    }
    [pscustomobject]@{ Targets = @($targets); Incomplete = $incomplete }
}

function Resolve-ProcessTree($Processes, [int]$RootPid) { (Resolve-ProcessTreeEx $Processes $RootPid).Targets }

function Get-ProcessListFromCim([int]$TimeoutSeconds = 0)
{
    $arguments = @{ Property = @('ProcessId', 'ParentProcessId', 'CreationDate'); ErrorAction = 'Stop' }
    if ($TimeoutSeconds -gt 0) { $arguments.OperationTimeoutSec = $TimeoutSeconds }
    @(Get-CimInstance Win32_Process @arguments |
        ForEach-Object { [pscustomobject]@{ Id = [int]$_.ProcessId; ParentId = [int]$_.ParentProcessId; Created = $_.CreationDate } })
}

function Get-ProcessList([scriptblock]$ListSource = $null, [int]$Attempts = 3, [int]$DelayMilliseconds = 200, [int]$TimeoutSeconds = 0)
{
    # A CIM query can fail transiently under load, so it is retried a bounded number of times before the error is thrown.
    $source = if ($ListSource) { $ListSource } else { { Get-ProcessListFromCim $TimeoutSeconds } }
    for ($attempt = 1; ; $attempt++)
    {
        try { return @(& $source) }
        catch
        {
            if ($attempt -ge $Attempts) { throw }
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

function Get-ProcessTreeSnapshot([int]$RootPid, [scriptblock]$ListSource = $null, [int]$Attempts = 3, [int]$DelayMilliseconds = 200)
{
    # The root and every descendant alive now, each with its creation time so that a reused id is not mistaken for it.
    Resolve-ProcessTree (Get-ProcessList $ListSource $Attempts $DelayMilliseconds) $RootPid
}

function Stop-ProcessIfSame($Entry, [long]$WaitMilliseconds)
{
    # Code as AwProcessJob.KillIfCreated: 0 gone, 1 unverified (never killed), 2 terminated but still alive, 3 the id now
    # belongs to a different process (target gone, holder untouched). Identity is the creation time compared exactly at
    # CIM's precision (whole microseconds, UTC); a conversion error is "unverified".
    Initialize-ProcessJob
    try { $expected = ([datetime]$Entry.Created).ToUniversalTime().ToFileTimeUtc() } catch { return 1 }
    [AwProcessJob]::KillIfCreated([int]$Entry.Id, ($expected - ($expected % 10)), $WaitMilliseconds)
}

function Stop-ProcessTreeById([int]$ProcessId, [int]$WaitSeconds = 10, [scriptblock]$ListSource = $null)
{
    # Fallback without a job object (a surviving executor found by recovery). One snapshot of the root and its descendants,
    # then each target is terminated through one handle that first confirms its identity (id plus creation time), no
    # taskkill /T. Guarantees: (a) a process that is not a snapshot target is never terminated; (b) true only when every
    # snapshot target is verified gone, or its id is verified to be held by a different process; any doubt (missing or
    # unreadable creation time, access denied, failed or overrun enumeration, targets still pending at the deadline) gives false. Not a goal:
    # descendants created after the snapshot, or whose parent died before it, are not found; only a job object contains them.
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    try { $tree = Resolve-ProcessTreeEx (Get-ProcessList $ListSource 3 200 ([Math]::Max(1, $WaitSeconds))) $ProcessId } catch { return $false }
    if ([DateTime]::UtcNow -ge $deadline) { return $false }
    if ($tree.Incomplete) { return $false }
    $pending = [Collections.Generic.List[object]]::new()
    foreach ($t in $tree.Targets) { $pending.Add($t) }
    while ($pending.Count -gt 0)
    {
        foreach ($entry in @($pending))
        {
            $remaining = [long][Math]::Max(0, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            $code = Stop-ProcessIfSame $entry ([Math]::Min(2000, $remaining))
            if ($code -eq 1) { return $false }
            if ($code -eq 0 -or $code -eq 3) { [void]$pending.Remove($entry) }
            if ($pending.Count -gt 0 -and [DateTime]::UtcNow -ge $deadline) { return $false }
        }
        if ($pending.Count -gt 0) { Start-Sleep -Milliseconds 100 }
    }
    $true
}
