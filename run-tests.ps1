param(
    # Optional test selector. Pass a class name (e.g. "ClockTests") or a
    # class+method (e.g. "ClockTests.SyncsWithinTolerance") to run a single
    # suite or test. Matched as a substring against the test's FullyQualifiedName,
    # so partial names work too. When omitted, runs the full inner-loop suite.
    [Parameter(Position=0)]
    [string]$Test,

    # Run inside a transient working-tree copy so multiple invocations can run
    # in parallel without colliding on the shared `.godot/mono/temp/bin/Debug`
    # build output dir, gdUnit4's per-assembly named pipe, or the `TestResults/`
    # artifact dir. See tools/Invoke-TestInWorktree.ps1 for the why.
    [switch]$Worktree,

    # Print usage and exit. -h is recognised as a short alias.
    [Alias('h')]
    [switch]$Help
)

if ($Help) {
    Write-Host @'
Usage: run-tests.ps1 [<TestSelector>] [-Worktree] [-Help]

Runs the inner-loop test suite (everything EXCEPT MonkeNet.Tests.MultiProcess).
The multi-process suite is excluded because each of its tests spawns separate
Godot child processes and adds several seconds each; use run-multiprocess-tests.ps1
to run those on demand.

Arguments:
  TestSelector   Optional substring matched against each test's
                 FullyQualifiedName via dotnet test's --filter. Accepts a class
                 name, a method name, or class.method. When omitted, runs the
                 full inner-loop suite. When passed, overrides the default
                 "exclude MultiProcess" filter -- if you really want a multi-
                 process test, name it explicitly here.

Options:
  -Worktree      Run inside a transient working-tree copy so multiple invocations
                 can run in parallel. The copy lives in $env:TEMP, has its own
                 .godot/ build output, its own TestResults/ artifact dir, and
                 its own gdUnit4 pipe (per-PID dll filename). Uncommitted/
                 untracked files ARE carried over. Artefacts are merged back
                 into tests/TestResults/ (per-test-class subdirs); when two
                 parallel runs target the same test class, the later writer
                 wins. Safe to run in parallel with run-multiprocess-tests.ps1
                 -Worktree.
  -Help, -h      Print this message and exit.

Outputs:
  test-output.log     stdout from `dotnet test`
  test-error.log      stderr from `dotnet test`
  exit code           propagated from `dotnet test`

Examples:
  run-tests.ps1
      Run the entire inner-loop suite (~3 minutes).

  run-tests.ps1 ClockTests
      Run every test inside the ClockTests class.

  run-tests.ps1 Clock_ImmediateCorrection_ClearsBuffersToAvoidWindowDoubleCount
      Run a single test by exact method name.

  run-tests.ps1 RollbackTests.InitialSnapshot_TriggersCatchUpWithoutMispredict
      Run a specific method via class.method.

  run-tests.ps1 NetworkConditions
      Run every test whose name contains "NetworkConditions" (the whole
      NetworkConditionTests class).

  run-tests.ps1 -Worktree ClockTests
      Run ClockTests inside a transient worktree. Safe to run alongside
      another test invocation in a separate terminal.
'@
    exit 0
}

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

# ── Worktree path ─────────────────────────────────────────────────────────
# Delegate to the shared helper. Same isolation mechanism as
# run-multiprocess-tests.ps1 -Worktree, just a different default filter.
if ($Worktree) {
    & (Join-Path $PSScriptRoot "tools\Invoke-TestInWorktree.ps1") `
        -Filter $filter `
        -StdoutLog "test-output.log" `
        -StderrLog "test-error.log" `
        -TimeoutMs 540000 `
        -Scenario "inner"
    exit $LASTEXITCODE
}

$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal --filter $filter" -RedirectStandardOutput "test-output.log" -RedirectStandardError "test-error.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(540000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output.log"
exit $proc.ExitCode
