$env:GODOT_BIN = "C:\Users\ivanz\Godot\godot-mp-modified\bin\godot.windows.editor.dev.x86_64.mono.console.exe"

$proc = Start-Process -FilePath "dotnet" -ArgumentList "test tests/MonkeNetTests.csproj --logger console;verbosity=normal" -RedirectStandardOutput "test-output.log" -RedirectStandardError "test-error.log" -NoNewWindow -PassThru

if (-not $proc.WaitForExit(120000)) {
    Write-Host "Timeout reached - killing test process"
    $proc.Kill($true)
    exit 1
}

Get-Content "test-output.log" | Select-String "Total tests:|  Passed |  Failed |Error Message:"
exit $proc.ExitCode
