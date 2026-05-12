param(
    # Optional test selector within the multi-process suite. Pass a class name
    # (e.g. "MultiProcessClockSyncTests") or class+method
    # (e.g. "MultiProcessClockSyncTests.ConvergesUnderJitter") to narrow the run.
    # Matched as a substring against the test's FullyQualifiedName. When omitted,
    # runs every test under MonkeNet.Tests.MultiProcess.
    [Parameter(Position=0)]
    [string]$Test
)

$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

# Runs the MonkeNet.Tests.MultiProcess suite — multi-process integration tests
# that spawn separate Godot child processes (one per server/client) for true
# OS-level isolation. These are slower and excluded from run-tests.ps1's fast
# inner-loop suite; this script runs them on demand.
#
# Each test spawns 2-3 Godot processes which take a few seconds each to come up,
# so the wall-clock budget is generous (5 minutes vs. run-tests.ps1's 2 minutes).
if ($Test) {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess&FullyQualifiedName~$Test"
} else {
    $filter = "FullyQualifiedName~MonkeNet.Tests.MultiProcess"
}

$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $filter" -RedirectStandardOutput "test-output-multiprocess.log" -RedirectStandardError "test-error-multiprocess.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(300000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output-multiprocess.log" | Select-String "Total tests:|  Passed |  Failed |Error Message:"
exit $proc.ExitCode
