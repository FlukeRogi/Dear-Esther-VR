# Runtime choice on a PC whose registered 32-bit OpenXR runtime is broken, as players have hit (xrCreateInstance
# error -32, XR_ERROR_FILE_ACCESS_ERROR). The registry isn't touched: Debug TestActiveRuntime stands in for the
# registered active runtime, and Debug TestVdxrRuntime puts the mock headset where VDXR would be.
#   powershell -ExecutionPolicy Bypass -File tests\runtime_fallback.ps1
. "$PSScriptRoot\lab.ps1"
$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++; Write-Output "PASS  $Name  $Detail" } else { $script:Fail++; Write-Output "FAIL  $Name  $Detail" }
}
Stop-Devr
Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
$savedCfg = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { $null }
Enable-Lab
Set-Head
$mock = "$Root\tools\mock_runtime.json"
# A 64-bit-only runtime, like SteamVR's own OpenXR runtime.
$fake64 = "$Lab\fake_runtime_x64.json"
'{ "file_format_version": "1.0.0", "runtime": { "name": "Fake64", "library_path": "C:\\Program Files (x86)\\Steam\\steamclient64.dll" } }' | Set-Content $fake64 -Encoding ascii
# A 32-bit DLL that isn't an OpenXR runtime, so the loader can't start it (the players' error).
$fakeBad = "$Lab\fake_runtime_bad.json"
('{ "file_format_version": "1.0.0", "runtime": { "name": "FakeBad", "library_path": "' + ("$ModDir\openvr_api.dll" -replace '\\', '\\') + '" } }') | Set-Content $fakeBad -Encoding ascii

$cases = [ordered]@{
    'registered runtime missing' = @{ Active = "$Lab\no_such_runtime.json"; Expect = "manifest file doesn't exist" }
    'registered runtime 64-bit only' = @{ Active = $fake64; Expect = '64-bit only' }
    'registered runtime fails to start (error -32)' = @{ Active = $fakeBad; Expect = "runtime itself couldn't start" }
}
try {
    foreach ($name in $cases.Keys) {
        $c = $cases[$name]
        Set-DevrKey 'OpenXR' 'Runtime' 'auto'
        Set-DevrKey 'Debug' 'TestActiveRuntime' $c.Active
        Set-DevrKey 'Debug' 'TestVdxrRuntime' $mock
        foreach ($f in 'cmd.txt', 'out.txt') { if (Test-Path "$Lab\$f") { [IO.File]::Delete("$Lab\$f") } }
        Start-Devr -GameArgs @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
        [void](Wait-Lab 120)
        $vr = ''
        for ($i = 0; $i -lt 20; $i++) { $vr = Lab 'vr' 1; if ($vr -match 'active=True') { break } }
        $log = Get-Content "$ModDir\DearEstherVR.log" -Raw
        Check "$name : explained in the log" ($log -match [regex]::Escape($c.Expect)) ''
        Check "$name : falls back and connects through VDXR" ($vr -match 'active=True' -and $log -match 'Connected through VDXR') ((([regex]::Matches($log, '\[(Info|Warn)\] (Runtime|Ways|Can''t|Connected)[^\r\n]*') | ForEach-Object { $_.Value }) -join ' | '))
        Check "$name : SteamVR was not started" (-not (Get-Process vrserver -ErrorAction SilentlyContinue)) ''
        Stop-Devr
        Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}
finally {
    Stop-Devr
    Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($null -ne $savedCfg) { Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline }
}
Write-Output ''
Write-Output "$script:Pass passed, $script:Fail failed"
