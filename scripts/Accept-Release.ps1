param([switch] $VerifyClaudeDesktop, [switch] $VerifyAntigravityDesktop)

if ($PSVersionTable.PSVersion.Major -lt 5) { throw 'Use Windows PowerShell 5.1 or PowerShell 7+ to run acceptance.' }
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $taskRoot
$taskRunId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$taskOutput = Join-Path $taskRoot "dist/candidate-$taskRunId"
if (Test-Path -LiteralPath $taskOutput) { throw "Release output already exists: $taskOutput" }
$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskBudget = [TimeSpan]::FromMinutes(5)
$taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskReportDirectory = Join-Path $taskRoot 'artifacts/acceptance'
New-Item -ItemType Directory -Path $taskReportDirectory -Force | Out-Null
$taskLog = Join-Path $taskReportDirectory "verification-$taskRunId.log"

function Quote-CmdArgument([string] $Value) {
    if ([string]::IsNullOrEmpty($Value)) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-SerialStep([string] $Label, [string] $Executable, [string[]] $Arguments) {
    if ($taskClock.Elapsed -ge $taskBudget) { throw 'Acceptance time budget exhausted.' }
    Write-Output $Label
    Add-Content -LiteralPath $taskLog -Value $Label
    $taskStart = [Diagnostics.ProcessStartInfo]::new($Executable)
    $taskStart.WorkingDirectory = $taskRoot
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    $taskStart.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $taskStart.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        foreach ($taskArgument in $Arguments) { $taskStart.ArgumentList.Add($taskArgument) }
    } else {
        $taskStart.Arguments = (($Arguments | ForEach-Object { Quote-CmdArgument $_ }) -join ' ')
    }
    $taskProcess = [Diagnostics.Process]::new()
    $taskProcess.StartInfo = $taskStart
    $taskOwned = @{}
    try {
        if (!$taskProcess.Start()) { throw "Could not start $Label" }
        $taskOwned[$taskProcess.Id] = $taskProcess.StartTime.ToUniversalTime()
        $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
        while (!$taskProcess.WaitForExit(250)) {
            $taskSnapshot = @(Get-CimInstance Win32_Process)
            do {
                $taskAdded = $false
                foreach ($taskChild in $taskSnapshot) {
                    $taskChildId = [int]$taskChild.ProcessId
                    $taskParentId = [int]$taskChild.ParentProcessId
                    if ($taskOwned.ContainsKey($taskChildId) -or !$taskOwned.ContainsKey($taskParentId)) { continue }
                    $taskParent = Get-Process -Id $taskParentId -ErrorAction SilentlyContinue
                    $taskChildProcess = Get-Process -Id $taskChildId -ErrorAction SilentlyContinue
                    try {
                        $taskParentStarted = if ($taskParent) { $taskParent.StartTime } else { $null }
                        $taskChildStarted = if ($taskChildProcess) { $taskChildProcess.StartTime } else { $null }
                        if ($taskParentStarted -is [DateTime] -and $taskChildStarted -is [DateTime] -and $taskParentStarted.ToUniversalTime() -eq $taskOwned[$taskParentId] -and $taskChildStarted.ToUniversalTime() -ge $taskOwned[$taskParentId]) {
                            $taskOwned[$taskChildId] = $taskChildStarted.ToUniversalTime()
                            $taskAdded = $true
                        }
                    } catch [System.InvalidOperationException] {
                    } catch [System.ComponentModel.Win32Exception] {
                    } finally { if ($taskParent) { $taskParent.Dispose() }; if ($taskChildProcess) { $taskChildProcess.Dispose() } }
                }
            } while ($taskAdded)
            if ($taskClock.Elapsed -ge $taskBudget) { throw "Acceptance timed out in $Label" }
        }
        $taskRemainingBudget = $taskBudget - $taskClock.Elapsed
        if ($taskRemainingBudget -le [TimeSpan]::Zero) { throw 'Acceptance output wait exceeded time budget.' }
        if (!$taskStdout.Wait($taskRemainingBudget) -or !$taskStderr.Wait($taskRemainingBudget)) {
            throw 'Acceptance output wait exceeded time budget.'
        }
        $taskText = $taskStdout.GetAwaiter().GetResult() + $taskStderr.GetAwaiter().GetResult()
        Write-Output $taskText
        Add-Content -LiteralPath $taskLog -Value $taskText
        if ($taskProcess.ExitCode -ne 0) { throw "$Label failed with exit code $($taskProcess.ExitCode). No automatic retry." }
    } finally {
        foreach ($taskIdentity in @($taskOwned.GetEnumerator())) {
            $taskRemaining = Get-Process -Id $taskIdentity.Key -ErrorAction SilentlyContinue
            if (!$taskRemaining) { continue }
            try {
                $taskRemainingStarted = $taskRemaining.StartTime
                if (!$taskRemaining.HasExited -and $taskRemainingStarted -is [DateTime] -and $taskRemainingStarted.ToUniversalTime() -eq $taskIdentity.Value) {
                    if ($PSVersionTable.PSVersion.Major -ge 7) {
                        $taskRemaining.Kill($true)
                    } else {
                        $taskRemaining.Kill()
                    }
                    if (!$taskRemaining.WaitForExit(2000)) { throw "Owned process did not exit: $($taskIdentity.Key)" }
                }
            } finally { $taskRemaining.Dispose() }
        }
        $taskProcess.Dispose()
    }
}

$taskBuildFlags = @('--disable-build-servers', '/m:1', '/nodeReuse:false', '/p:UseSharedCompilation=false', '-v:minimal')
Invoke-SerialStep 'Build solution and contract tests' $taskDotnet (@('build', 'Hyperion.sln', '-c', 'Release') + $taskBuildFlags)
Invoke-SerialStep 'Run contracts, real isolated app-server integration, and WPF layout rendering' $taskDotnet @('tests/CodexMultiLauncher.ContractTests/bin/Release/net10.0-windows/CodexMultiLauncher.ContractTests.dll')
if ($VerifyClaudeDesktop) {
    Invoke-SerialStep 'Verify two isolated Claude Desktop windows without login or model requests' (Join-Path $taskRoot 'tests/CodexMultiLauncher.ContractTests/bin/Release/net10.0-windows/CodexMultiLauncher.ContractTests.exe') @('--claude-smoke')
}
if ($VerifyAntigravityDesktop) {
    Invoke-SerialStep 'Verify two isolated Antigravity windows without login or model requests' (Join-Path $taskRoot 'tests/CodexMultiLauncher.ContractTests/bin/Release/net10.0-windows/CodexMultiLauncher.ContractTests.exe') @('--antigravity-smoke')
}
Invoke-SerialStep 'Publish portable release' $taskDotnet (@('publish', 'src/CodexMultiLauncher/CodexMultiLauncher.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '/p:PublishSingleFile=true', '/p:EnableCompressionInSingleFile=true', '/p:DebugType=None', '/p:DebugSymbols=false', '-o', $taskOutput) + $taskBuildFlags)
$legacyExe = Join-Path $taskOutput 'CodexMultiLauncher.exe'
$brandedExe = Join-Path $taskOutput 'Hyperion.exe'
if (Test-Path -LiteralPath $legacyExe) {
    Move-Item -LiteralPath $legacyExe -Destination $brandedExe -Force
}
Write-Output "Acceptance completed. Candidate: $taskOutput"
Add-Content -LiteralPath $taskLog -Value "Acceptance completed in $($taskClock.Elapsed)."
