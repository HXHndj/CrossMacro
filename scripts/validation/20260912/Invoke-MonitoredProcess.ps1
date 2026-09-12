[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$FileName = 'dotnet',

    [Parameter(Position = 1)]
    [string[]]$ArgumentList = @(),

    [string]$WorkingDirectory = '',

    [string]$OutputDirectory = '',

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$RunName = 'run',

    [ValidateRange(500, 1000)]
    [int]$SampleIntervalMilliseconds = 750,

    [ValidateRange(1, 86400)]
    [int]$TimeoutSeconds = 1800,

    # PrivateMemorySize64 is the budget metric. WorkingSet64 and the per-process
    # sum are recorded as independent evidence in every sample and in the peak.
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MemoryBudgetBytes = 1610612736,

    [ValidateSet('PrivateBytes', 'WorkingSetBytes', 'CombinedBytes')]
    [string]$BudgetMetric = 'PrivateBytes',

    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Show-Usage {
    @'
Usage: Invoke-MonitoredProcess.ps1 [-FileName <path>] [-ArgumentList <string[]>]
  [-WorkingDirectory <path>] [-OutputDirectory <path>] [-RunName <name>]
  [-SampleIntervalMilliseconds <500..1000>] [-TimeoutSeconds <seconds>]
  [-MemoryBudgetBytes <bytes>] [-BudgetMetric <PrivateBytes|WorkingSetBytes|CombinedBytes>]

Starts one process with a hidden window, captures stdout/stderr, samples the root
PID and its descendants, and writes JSONL samples plus a JSON summary. The default
budget is 1.5 GiB of aggregate PrivateMemorySize64 (private commit). The runner
 holds a session-local single-instance mutex and terminates only the monitored
process tree on timeout or budget breach.

Typical validation invocation:
  pwsh -NoProfile -File scripts/validation/20260912/Invoke-MonitoredProcess.ps1 `
    -FileName dotnet -ArgumentList @('test','tests/CrossMacro.UI.Tests/CrossMacro.UI.Tests.csproj',
      '--configuration','Release','--no-restore','--no-build','--disable-build-servers','/m:1') `
    -WorkingDirectory (Get-Location).Path -RunName ui-round1
'@
}

function ConvertTo-WindowsArgument {
    param([AllowNull()][string]$Value)

    if ($null -eq $Value) {
        $Value = ''
    }

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append([char]34)
    $backslashCount = 0

    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashCount++
            continue
        }

        if ($character -eq [char]34) {
            for ($index = 0; $index -lt (($backslashCount * 2) + 1); $index++) {
                [void]$builder.Append([char]92)
            }

            [void]$builder.Append([char]34)
            $backslashCount = 0
            continue
        }

        for ($index = 0; $index -lt $backslashCount; $index++) {
            [void]$builder.Append([char]92)
        }

        [void]$builder.Append($character)
        $backslashCount = 0
    }

    # Backslashes immediately before the closing quote must be doubled.
    for ($index = 0; $index -lt ($backslashCount * 2); $index++) {
        [void]$builder.Append([char]92)
    }

    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function ConvertTo-HumanCommandLine {
    param([string[]]$Arguments)

    if ($null -eq $Arguments -or $Arguments.Count -eq 0) {
        return ''
    }

    return (($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')
}

function Get-ProcessParentSnapshot {
    $parentMap = @{}
    $errorMessages = [System.Collections.Generic.List[string]]::new()

    try {
        foreach ($record in @(Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId -ErrorAction Stop)) {
            $processId = [int]$record.ProcessId
            if ($processId -gt 0) {
                $parentMap[$processId] = [int]$record.ParentProcessId
            }
        }
    }
    catch {
        [void]$errorMessages.Add("CIM: $($_.Exception.Message)")
    }

    if ($parentMap.Count -eq 0) {
        try {
            $nativeType = [System.Management.Automation.PSTypeName]'CrossMacro.Validation.ProcessTreeNative'
            if ($null -eq $nativeType.Type) {
                Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrossMacro.Validation;

public static class ProcessTreeNative
{
    private const uint SnapshotProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static Dictionary<int, int> GetParentMap()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");
        }

        try
        {
            var result = new Dictionary<int, int>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process32FirstW failed");
            }

            do
            {
                if (entry.ProcessId > 0)
                {
                    result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }
}
'@
            }

            foreach ($entry in [CrossMacro.Validation.ProcessTreeNative]::GetParentMap().GetEnumerator()) {
                $parentMap[[int]$entry.Key] = [int]$entry.Value
            }
        }
        catch {
            [void]$errorMessages.Add("Toolhelp32: $($_.Exception.Message)")
        }
    }

    return [pscustomobject]@{
        ParentMap = $parentMap
        Error = if ($errorMessages.Count -gt 0 -and $parentMap.Count -eq 0) { $errorMessages -join '; ' } else { $null }
    }
}

function Get-ProcessStartToken {
    param([System.Diagnostics.Process]$Process)

    try {
        return $Process.StartTime.ToUniversalTime().Ticks
    }
    catch {
        return $null
    }
}

function Add-DescendantsToTracking {
    param(
        [Parameter(Mandatory = $true)][int]$RootPid,
        [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[int]]$KnownProcessIds,
        [Parameter(Mandatory = $true)][hashtable]$ParentMap
    )

    $pending = [System.Collections.Generic.Queue[int]]::new()
    [void]$pending.Enqueue($RootPid)
    foreach ($knownPid in @($KnownProcessIds)) {
        [void]$pending.Enqueue([int]$knownPid)
    }

    while ($pending.Count -gt 0) {
        $parentPid = $pending.Dequeue()
        foreach ($entry in @($ParentMap.GetEnumerator())) {
            if ([int]$entry.Value -eq $parentPid) {
                $childPid = [int]$entry.Key
                if ($KnownProcessIds.Add($childPid)) {
                    [void]$pending.Enqueue($childPid)
                }
            }
        }
    }
}

function Get-TrackedProcessSample {
    param(
        [Parameter(Mandatory = $true)][int]$RootPid,
        [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[int]]$KnownProcessIds,
        [Parameter(Mandatory = $true)][hashtable]$StartTokens,
        [int]$SampleIndex
    )

    $parentSnapshot = Get-ProcessParentSnapshot
    Add-DescendantsToTracking -RootPid $RootPid -KnownProcessIds $KnownProcessIds -ParentMap $parentSnapshot.ParentMap

    $processRows = [System.Collections.Generic.List[object]]::new()
    $sampleErrors = [System.Collections.Generic.List[string]]::new()
    if ($parentSnapshot.Error) {
        [void]$sampleErrors.Add("process-parent discovery: $($parentSnapshot.Error)")
    }

    foreach ($trackedPid in @($KnownProcessIds)) {
        $process = $null
        try {
            $process = [System.Diagnostics.Process]::GetProcessById([int]$trackedPid)
            $process.Refresh()
            $startToken = Get-ProcessStartToken -Process $process
            if ($StartTokens.ContainsKey([int]$trackedPid) -and $null -ne $startToken -and $StartTokens[[int]$trackedPid] -ne $startToken) {
                [void]$sampleErrors.Add("PID ${trackedPid} was reused; ignored")
                continue
            }

            if (-not $StartTokens.ContainsKey([int]$trackedPid) -and $null -ne $startToken) {
                $StartTokens[[int]$trackedPid] = $startToken
            }

            if ($process.HasExited) {
                continue
            }

            $workingSetBytes = [int64]$process.WorkingSet64
            $privateBytes = [int64]$process.PrivateMemorySize64
            [void]$processRows.Add([ordered]@{
                    pid = [int]$trackedPid
                    name = $process.ProcessName
                    parentPid = if ($parentSnapshot.ParentMap.ContainsKey([int]$trackedPid)) { [int]$parentSnapshot.ParentMap[[int]$trackedPid] } else { $null }
                    workingSet64Bytes = $workingSetBytes
                    privateMemorySize64Bytes = $privateBytes
                    combinedBytes = $workingSetBytes + $privateBytes
                })
        }
        catch [System.ArgumentException] {
            # The process exited between enumeration and GetProcessById.
        }
        catch {
            [void]$sampleErrors.Add("PID ${trackedPid}: $($_.Exception.Message)")
        }
        finally {
            if ($null -ne $process) {
                $process.Dispose()
            }
        }
    }

    $workingSetTotal = [int64]0
    $privateTotal = [int64]0
    foreach ($row in $processRows) {
        $workingSetTotal += [int64]$row.workingSet64Bytes
        $privateTotal += [int64]$row.privateMemorySize64Bytes
    }

    return [pscustomobject]@{
        Sample = [ordered]@{
            sampleIndex = $SampleIndex
            timestampUtc = [DateTime]::UtcNow.ToString('o')
            rootPid = $RootPid
            trackedPidCount = $KnownProcessIds.Count
            liveProcessCount = $processRows.Count
            treeWorkingSetBytes = $workingSetTotal
            treePrivateBytes = $privateTotal
            treeCombinedBytes = $workingSetTotal + $privateTotal
            processes = @($processRows)
            errors = @($sampleErrors)
        }
        LiveProcessCount = $processRows.Count
        TreeWorkingSetBytes = $workingSetTotal
        TreePrivateBytes = $privateTotal
        TreeCombinedBytes = $workingSetTotal + $privateTotal
    }
}

function Get-CurrentDescendantIds {
    param(
        [Parameter(Mandatory = $true)][int]$RootPid,
        [Parameter(Mandatory = $true)][hashtable]$ParentMap
    )

    $result = [System.Collections.Generic.HashSet[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    [void]$pending.Enqueue($RootPid)

    while ($pending.Count -gt 0) {
        $parentPid = $pending.Dequeue()
        foreach ($entry in @($ParentMap.GetEnumerator())) {
            if ([int]$entry.Value -eq $parentPid) {
                $childPid = [int]$entry.Key
                if ($result.Add($childPid)) {
                    [void]$pending.Enqueue($childPid)
                }
            }
        }
    }

    return $result
}

function Stop-MonitoredProcessTree {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$RootProcess,
        [Parameter(Mandatory = $true)][int]$RootPid,
        [Parameter(Mandatory = $true)][hashtable]$StartTokens
    )

    $actions = [System.Collections.Generic.List[object]]::new()
    $parentSnapshot = Get-ProcessParentSnapshot
    $descendantIds = Get-CurrentDescendantIds -RootPid $RootPid -ParentMap $parentSnapshot.ParentMap

    try {
        if (-not $RootProcess.HasExited) {
            $RootProcess.Kill($true)
            [void]$actions.Add([ordered]@{ pid = $RootPid; action = 'kill-entire-process-tree'; result = 'requested' })
        }
    }
    catch {
        [void]$actions.Add([ordered]@{ pid = $RootPid; action = 'kill-entire-process-tree'; result = 'error'; error = $_.Exception.Message })
    }

    # A root process can exit before descendants do. Kill only PIDs that are still
    # current descendants and whose start token matches the process we launched.
    foreach ($descendantPid in @($descendantIds)) {
        $process = $null
        try {
            $process = [System.Diagnostics.Process]::GetProcessById([int]$descendantPid)
            $process.Refresh()
            $startToken = Get-ProcessStartToken -Process $process
            if ($StartTokens.ContainsKey([int]$descendantPid) -and $null -ne $startToken -and $StartTokens[[int]$descendantPid] -ne $startToken) {
                [void]$actions.Add([ordered]@{ pid = [int]$descendantPid; action = 'skip-reused-pid'; result = 'skipped' })
                continue
            }

            if (-not $process.HasExited) {
                $process.Kill()
                [void]$actions.Add([ordered]@{ pid = [int]$descendantPid; action = 'kill-descendant'; result = 'requested' })
            }
        }
        catch [System.ArgumentException] {
            [void]$actions.Add([ordered]@{ pid = [int]$descendantPid; action = 'kill-descendant'; result = 'already-exited' })
        }
        catch {
            [void]$actions.Add([ordered]@{ pid = [int]$descendantPid; action = 'kill-descendant'; result = 'error'; error = $_.Exception.Message })
        }
        finally {
            if ($null -ne $process) {
                $process.Dispose()
            }
        }
    }

    return @($actions)
}

if ($Help) {
    Show-Usage
    exit 0
}

if ($MemoryBudgetBytes -le 0) {
    throw 'MemoryBudgetBytes must be greater than zero.'
}

$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDirectory '../../..')).Path
$resolvedWorkingDirectory = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $repositoryRoot
}
else {
    (Resolve-Path -LiteralPath $WorkingDirectory).Path
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts/20260912/validation'
}

$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$mutex = $null
$lockHeld = $false

try {
    $mutex = [System.Threading.Mutex]::new($false, 'Local\CrossMacro.Validation.SerialRunner')
    try {
        $lockHeld = $mutex.WaitOne(0, $false)
    }
    catch [System.Threading.AbandonedMutexException] {
        $lockHeld = $true
    }

    if (-not $lockHeld) {
        Write-Error 'A monitored validation process is already running (Local\CrossMacro.Validation.SerialRunner).'
        exit 73
    }

    New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
    $runStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $runDirectory = Join-Path $resolvedOutputRoot "$RunName-$runStamp-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

    $stdoutPath = Join-Path $runDirectory 'stdout.log'
    $stderrPath = Join-Path $runDirectory 'stderr.log'
    $samplesPath = Join-Path $runDirectory 'samples.jsonl'
    $summaryPath = Join-Path $runDirectory 'summary.json'
    $commandPath = Join-Path $runDirectory 'command.json'

    $summary = [ordered]@{
        schemaVersion = 1
        status = 'starting'
        fileName = $FileName
        argumentList = @($ArgumentList)
        humanCommandLine = "$(ConvertTo-WindowsArgument $FileName) $(ConvertTo-HumanCommandLine $ArgumentList)".Trim()
        workingDirectory = $resolvedWorkingDirectory
        runName = $RunName
        outputDirectory = $runDirectory
        stdoutPath = $stdoutPath
        stderrPath = $stderrPath
        samplesPath = $samplesPath
        commandPath = $commandPath
        sampleIntervalMilliseconds = $SampleIntervalMilliseconds
        timeoutSeconds = $TimeoutSeconds
        memoryBudgetBytes = $MemoryBudgetBytes
        memoryBudgetGiB = [Math]::Round($MemoryBudgetBytes / 1GB, 3)
        budgetMetric = $BudgetMetric
        rootPid = $null
        startTimeUtc = $null
        endTimeUtc = $null
        exitCode = $null
        timedOut = $false
        budgetExceeded = $false
        budgetExceededAtUtc = $null
        processStartMode = $null
        peak = $null
        termination = @()
        errors = @()
    }

    $commandRecord = [ordered]@{
        schemaVersion = 1
        fileName = $FileName
        argumentList = @($ArgumentList)
        humanCommandLine = $summary.humanCommandLine
        workingDirectory = $resolvedWorkingDirectory
        environmentIsolation = 'Caller supplies isolated APPDATA/LOCALAPPDATA and other test-only variables when needed.'
        startRequestedUtc = [DateTime]::UtcNow.ToString('o')
    }
    Set-Content -LiteralPath $commandPath -Value ($commandRecord | ConvertTo-Json -Depth 5) -Encoding UTF8

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.WorkingDirectory = $resolvedWorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = $null
    $stdoutStream = $null
    $stderrStream = $null
    $stdoutCopyTask = $null
    $stderrCopyTask = $null
    $usesProcessStartInfo = $false
    $knownProcessIds = [System.Collections.Generic.HashSet[int]]::new()
    $startTokens = @{}
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $sampleIndex = 0
    $peak = [ordered]@{
        sampleIndex = $null
        timestampUtc = $null
        treeWorkingSetBytes = [int64]0
        treePrivateBytes = [int64]0
        treeCombinedBytes = [int64]0
        liveProcessCount = 0
        trackedPidCount = 0
    }

    if ($null -ne $startInfo.PSObject.Properties['ArgumentList']) {
        foreach ($argument in @($ArgumentList)) {
            [void]$startInfo.ArgumentList.Add([string]$argument)
        }

        # Copy the redirected streams with framework tasks. PowerShell script
        # blocks cannot safely run on the DataReceived callback thread because
        # that thread has no PowerShell runspace.
        $stdoutStream = [System.IO.FileStream]::new($stdoutPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 4096, $true)
        $stderrStream = [System.IO.FileStream]::new($stderrPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read, 4096, $true)
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "Failed to start process '$FileName'."
        }
        $usesProcessStartInfo = $true
        $summary.processStartMode = 'ProcessStartInfo.ArgumentList/CreateNoWindow'
        [void]($stdoutCopyTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutStream))
        [void]($stderrCopyTask = $process.StandardError.BaseStream.CopyToAsync($stderrStream))
    }
    else {
        # Compatibility fallback for an older PowerShell/.NET host. ArgumentList
        # is preferred above; this path still uses a single correctly escaped
        # command line and the required hidden Start-Process window.
        $escapedArguments = ConvertTo-HumanCommandLine $ArgumentList
        $process = Start-Process -FilePath $FileName -ArgumentList $escapedArguments `
            -WorkingDirectory $resolvedWorkingDirectory `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
            -WindowStyle Hidden -PassThru
        $summary.processStartMode = 'Start-Process/-WindowStyle Hidden fallback'
    }

    $rootPid = [int]$process.Id
    $knownProcessIds.Add($rootPid) | Out-Null
    $rootStartToken = Get-ProcessStartToken -Process $process
    if ($null -ne $rootStartToken) {
        $startTokens[$rootPid] = $rootStartToken
    }
    $summary.rootPid = $rootPid
    $summary.startTimeUtc = [DateTime]::UtcNow.ToString('o')
    $summary.status = 'running'

    $sampleWriter = [System.IO.StreamWriter]::new($samplesPath, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        while ($true) {
            $processSample = Get-TrackedProcessSample -RootPid $rootPid -KnownProcessIds $knownProcessIds -StartTokens $startTokens -SampleIndex $sampleIndex
            $sample = $processSample.Sample
            $metricValue = switch ($BudgetMetric) {
                'PrivateBytes' { $processSample.TreePrivateBytes }
                'WorkingSetBytes' { $processSample.TreeWorkingSetBytes }
                'CombinedBytes' { $processSample.TreeCombinedBytes }
            }
            $sample.budgetMetricValueBytes = $metricValue
            $sample.memoryBudgetBytes = $MemoryBudgetBytes
            $sample.budgetExceeded = $metricValue -gt $MemoryBudgetBytes
            $sampleWriter.WriteLine(($sample | ConvertTo-Json -Compress -Depth 7))
            $sampleWriter.Flush()

            $newPeak = $processSample.TreeWorkingSetBytes -gt $peak.treeWorkingSetBytes -or
                $processSample.TreePrivateBytes -gt $peak.treePrivateBytes -or
                $processSample.TreeCombinedBytes -gt $peak.treeCombinedBytes -or
                $processSample.LiveProcessCount -gt $peak.liveProcessCount -or
                $knownProcessIds.Count -gt $peak.trackedPidCount
            if ($newPeak) {
                $peak.sampleIndex = $sample.sampleIndex
                $peak.timestampUtc = $sample.timestampUtc
            }
            if ($processSample.TreeWorkingSetBytes -gt $peak.treeWorkingSetBytes) { $peak.treeWorkingSetBytes = $processSample.TreeWorkingSetBytes }
            if ($processSample.TreePrivateBytes -gt $peak.treePrivateBytes) { $peak.treePrivateBytes = $processSample.TreePrivateBytes }
            if ($processSample.TreeCombinedBytes -gt $peak.treeCombinedBytes) { $peak.treeCombinedBytes = $processSample.TreeCombinedBytes }
            if ($processSample.LiveProcessCount -gt $peak.liveProcessCount) { $peak.liveProcessCount = $processSample.LiveProcessCount }
            if ($knownProcessIds.Count -gt $peak.trackedPidCount) { $peak.trackedPidCount = $knownProcessIds.Count }
            if ($metricValue -gt $MemoryBudgetBytes) {
                $summary.status = 'budget-exceeded'
                $summary.budgetExceeded = $true
                $summary.budgetExceededAtUtc = [DateTime]::UtcNow.ToString('o')
                $summary.errors = @($summary.errors + @($sample.errors))
                $summary.termination = Stop-MonitoredProcessTree -RootProcess $process -RootPid $rootPid -StartTokens $startTokens
                $summary.exitCode = 125
                break
            }

            $rootExited = $false
            try {
                $rootExited = $process.HasExited
            }
            catch {
                $rootExited = $true
            }

            if ($rootExited -and $processSample.LiveProcessCount -eq 0) {
                $summary.status = 'completed'
                break
            }

            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $summary.status = 'timeout'
                $summary.timedOut = $true
                $summary.termination = Stop-MonitoredProcessTree -RootProcess $process -RootPid $rootPid -StartTokens $startTokens
                $summary.exitCode = 124
                break
            }

            $sampleIndex++
            Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        }
    }
    finally {
        $sampleWriter.Dispose()
    }

    try {
        [void]$process.WaitForExit(10000)
    }
    catch {
        $summary.errors = @($summary.errors + "WaitForExit: $($_.Exception.Message)")
    }

    if ($summary.status -eq 'completed') {
        try {
            $summary.exitCode = [int]$process.ExitCode
            if ($summary.exitCode -ne 0) {
                $summary.status = 'process-failed'
            }
        }
        catch {
            $summary.errors = @($summary.errors + "ExitCode: $($_.Exception.Message)")
            $summary.exitCode = 1
            $summary.status = 'process-failed'
        }
    }

    $summary.peak = $peak
    $summary.endTimeUtc = [DateTime]::UtcNow.ToString('o')
    if ($summary.errors.Count -eq 0) {
        $summary.errors = @()
    }
}
catch {
    if ($null -ne $summary) {
        $summary.status = 'runner-failed'
        $summary.errors = @($summary.errors + $_.Exception.ToString())
        $summary.exitCode = 1
        $summary.endTimeUtc = [DateTime]::UtcNow.ToString('o')
        if ($null -ne $peak) {
            $summary.peak = $peak
        }
    }
    else {
        Write-Error $_
    }
    $finalExitCode = 1
}
finally {
    if ($null -ne $process) {
        if ($usesProcessStartInfo) {
            try { [void]$process.WaitForExit(2000) } catch { }
        }
    }
    if ($null -ne $stdoutCopyTask) {
        try { [void]$stdoutCopyTask.GetAwaiter().GetResult() } catch { if ($null -ne $summary) { $summary.errors = @($summary.errors + "stdout capture: $($_.Exception.Message)") } }
    }
    if ($null -ne $stderrCopyTask) {
        try { [void]$stderrCopyTask.GetAwaiter().GetResult() } catch { if ($null -ne $summary) { $summary.errors = @($summary.errors + "stderr capture: $($_.Exception.Message)") } }
    }
    if ($null -ne $stdoutStream) {
        try { $stdoutStream.Dispose() } catch { }
    }
    if ($null -ne $stderrStream) {
        try { $stderrStream.Dispose() } catch { }
    }
    if ($null -ne $process) {
        try { $process.Dispose() } catch { }
    }

    if ($null -ne $summaryPath -and $null -ne $summary) {
        try {
            Set-Content -LiteralPath $summaryPath -Value ($summary | ConvertTo-Json -Depth 10) -Encoding UTF8
        }
        catch {
            Write-Error "Could not write runner summary '$summaryPath': $($_.Exception.Message)"
        }
    }

    if ($lockHeld -and $null -ne $mutex) {
        try { $mutex.ReleaseMutex() } catch { }
    }
    if ($null -ne $mutex) {
        $mutex.Dispose()
    }
}

if ($null -ne $summary) {
    $finalExitCode = if ($null -ne $summary.exitCode) { [int]$summary.exitCode } else { 1 }
    Write-Output ($summary | ConvertTo-Json -Depth 10)
}
else {
    $finalExitCode = 1
}

exit $finalExitCode
