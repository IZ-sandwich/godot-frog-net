param(
    # Optional test selector. Pass a class name (e.g. "ClockTests") or a
    # class+method (e.g. "ClockTests.SyncsWithinTolerance") to run a single
    # suite or test. Matched as a substring against the test's FullyQualifiedName,
    # so partial names work too. When omitted, runs the full inner-loop suite.
    [Parameter(Position=0)]
    [string]$Test
)

$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

# Gate to suppress demo behaviors that grab user-facing input during tests.
# FirstPersonCameraController checks this before calling Input.MouseMode = Captured;
# without it, any test that drives a real client-handshake (ListenServerTests,
# NetworkConditionTests, NetworkedRigidbodyTests, ...) would lock the user's mouse
# inside the gdUnit4-spawned Godot window for the duration of the run.
$env:MONKENET_TEST = "1"

# Default filter excludes the multi-process suite (MonkeNet.Tests.MultiProcess) —
# those tests spawn separate Godot child processes and take significantly longer;
# run them via run-multiprocess-tests.ps1 instead. When a -Test selector is
# provided, the user is being explicit, so honor it as-is (even if it matches
# MultiProcess).
if ($Test) {
    $filter = "FullyQualifiedName~$Test"
} else {
    $filter = "FullyQualifiedName!~MonkeNet.Tests.MultiProcess"
}

$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $filter" -RedirectStandardOutput "test-output.log" -RedirectStandardError "test-error.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(540000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output.log" | Select-String "Total tests:|  Passed |  Failed |Error Message:"
exit $proc.ExitCode
