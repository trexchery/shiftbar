# Fake claude-swap engine for the Auto-Prime tests. Synthetic only: no credentials anywhere.
# Answers only an Auto-Prime request (`run N ... -- -p Hi ... --no-session-persistence`); anything else exits 2.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$a = $args
$log = $env:FAKE_LOG
# Concurrent calls share the logs: retry a busy file rather than fail the call over a log line.
function Log($path, $text) {
    for ($i = 0; $i -lt 40; $i++) {
        try { [IO.File]::AppendAllText($path, "$text`r`n"); return } catch { Start-Sleep -Milliseconds 25 }
    }
}
if ($log) { Log $log ($a -join ' ') }

function Emit($o) { [Console]::Out.WriteLine(($o | ConvertTo-Json -Compress -Depth 10)); [Console]::Out.Flush() }

# An Auto-Prime request (`run N … -- -p Hi … --no-session-persistence`): logs which auth variables
# reached it (presence only) and answers like `claude -p --output-format json`. FAKE_PRIME_FAIL refuses it, FAKE_PRIME_CRASH fails it after the launch.
if ($a[0] -eq 'run' -and $a -contains '--no-session-persistence') {
    if ($log) { Log "$log.prime" ("{0}|{1}|{2}|{3}" -f [bool]$env:ANTHROPIC_API_KEY, [bool]$env:CLAUDE_CONFIG_DIR, [bool]$env:CLAUDECODE, (Get-Location).Path) }
    if ($env:FAKE_PRIME_FAIL) { [Console]::Error.WriteLine("Error: $env:FAKE_PRIME_FAIL"); exit 1 } # the engine refused: Claude never started
    [Console]::Out.WriteLine("Launching Account-$($a[1]) (fake) [session mode]")
    if ($env:FAKE_PRIME_CRASH) { [Console]::Error.WriteLine("Error: $env:FAKE_PRIME_CRASH"); exit 1 } # Claude started, then failed
    Emit @{ type = 'result'; subtype = 'success'; is_error = $false; num_turns = 1; result = 'Hello!' }
    exit 0
}

exit 2
