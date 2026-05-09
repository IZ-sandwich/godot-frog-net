$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

# Gate to suppress demo behaviors that grab user-facing input during tests.
# FirstPersonCameraController checks this before calling Input.MouseMode = Captured;
# without it, any test that drives a real client-handshake (ListenServerTests,
# NetworkConditionTests, NetworkedRigidbodyTests, ...) would lock the user's mouse
# inside the gdUnit4-spawned Godot window for the duration of the run.
$env:MONKENET_TEST = "1"

# Excludes the multi-process suite (MonkeNet.Tests.MultiProcess) — those tests
# spawn separate Godot child processes and take significantly longer; run them
# via run-multiprocess-tests.ps1 instead.
$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter FullyQualifiedName!~MonkeNet.Tests.MultiProcess" -RedirectStandardOutput "test-output.log" -RedirectStandardError "test-error.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(540000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output.log" | Select-String "Total tests:|  Passed |  Failed |Error Message:"
exit $proc.ExitCode
