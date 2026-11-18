# SteamVR (OpenVR) path test, on SteamVR's built-in null HMD. Needs no headset.
#   powershell -ExecutionPolicy Bypass -File tests\steamvr_test.ps1
# SteamVR's settings are backed up and restored by steamvr_null.ps1, and the mod's settings are put back after.
. "$PSScriptRoot\lab.ps1"
$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++; Write-Output "PASS  $Name  $Detail" } else { $script:Fail++; Write-Output "FAIL  $Name  $Detail" }
}
function Val([string]$Text, [string]$Key) { [regex]::Match($Text, "\b$Key=([^\s]+)").Groups[1].Value }
function Num([string]$Text, [string]$Key) { $v = 0.0; if ([double]::TryParse((Val $Text $Key), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v)) { $v } else { [double]::NaN } }
function Crashed { (Select-String -Path "$Game\DearEsther_Data\output_log.txt" -Pattern 'Crash!!!' -ErrorAction SilentlyContinue).Count -gt 0 }

Stop-Devr
Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
$savedCfg = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { $null }
& "$PSScriptRoot\steamvr_null.ps1" -Enable
try {
    Enable-Lab
    Set-DevrKey 'OpenXR' 'Runtime' 'steamvr'
    Set-DevrKey 'Debug' 'Skip' ''
    foreach ($f in 'cmd.txt', 'out.txt') { if (Test-Path "$Lab\$f") { [IO.File]::Delete("$Lab\$f") } }
    Start-Devr
    [void](Wait-Lab 240)
    # The null HMD has no room set up, so SteamVR opens Room Setup, which takes the headset from the game. A real
    # headset has a room. Closing it here also shows the game reconnecting once the headset is free.
    $vr = ''; $closedRoomSetup = $false
    for ($i = 0; $i -lt 45; $i++) {
        $rs = Get-Process steamvr_room_setup -ErrorAction SilentlyContinue
        if ($rs) { $rs | Stop-Process -Force; $closedRoomSetup = $true }
        $vr = Lab 'vr' 2
        if ((Val $vr 'active') -eq 'True' -and -not (Get-Process steamvr_room_setup -ErrorAction SilentlyContinue)) { Start-Sleep 3; $vr = Lab 'vr' 1; if ((Val $vr 'active') -eq 'True') { break } }
    }
    Check 'SteamVR session starts through OpenVR' ((Val $vr 'active') -eq 'True' -and (Val $vr 'runtime') -match 'SteamVR') ("runtime=" + (Val $vr 'runtime') + $(if ($closedRoomSetup) { ', after closing Room Setup' } else { '' }))
    $a = Num $vr 'submitted'; Start-Sleep 3; $vr = Lab 'vr'; $b = Num $vr 'submitted'
    Check 'Frames reach the SteamVR compositor' ($b -gt $a + 60) ("$a -> $b, {0:F0} per second" -f (($b - $a) / 3))
    Check 'Eye textures follow the headset''s recommended size' ((Val $vr 'eye') -match '^\d+x\d+$') ("eye=" + (Val $vr 'eye'))
    Lab 'load Donnelley' 1 | Out-Null
    for ($i = 0; $i -lt 40 -and (Lab 'state' 1) -notmatch 'player=FPSCamera'; $i++) { }
    Start-Sleep 8
    $vr = Lab 'vr'
    $e0 = (Val $vr 'eye0').Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $e1 = (Val $vr 'eye1').Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $sep = [math]::Sqrt(($e0[0] - $e1[0]) * ($e0[0] - $e1[0]) + ($e0[1] - $e1[1]) * ($e0[1] - $e1[1]) + ($e0[2] - $e1[2]) * ($e0[2] - $e1[2]))
    Check 'Tracked poses arrive, with two separate eyes' ($sep -gt 0.04 -and $sep -lt 0.09) ("eye separation {0:F4}" -f $sep)
    Check 'Chapter 1 plays through SteamVR' ((Lab 'state') -match 'chapter=True' -and (Val $vr 'active') -eq 'True') ''
    Check 'Gameplay frame rate on SteamVR' ((Num $vr 'fps') -gt 45) ("fps={0:F0} height={1} floorKnown={2}" -f (Num $vr 'fps'), (Val $vr 'height'), (Val $vr 'floorKnown'))
    foreach ($r in '1600 900 0', '1280 720 0') { Lab "res $r" 1 | Out-Null; Start-Sleep 3 }
    $a = Num (Lab 'vr') 'submitted'; Start-Sleep 2; $b = Num (Lab 'vr') 'submitted'
    Check 'Window size changes during SteamVR play: frames continue' (-not (Crashed) -and $b -gt $a + 30) "$a -> $b"
    $log = Get-Content "$ModDir\DearEstherVR.log" -Raw
    Check 'No compositor or overlay errors' ($log -notmatch 'Submit failed|CreateOverlay failed|\[Error\]') ''
    Check 'The game never crashed' (-not (Crashed)) ''
}
finally {
    Stop-Devr
    Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($null -ne $savedCfg) { Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline }
    & "$PSScriptRoot\steamvr_null.ps1" -Disable
    Get-ChildItem $Game -Directory | Where-Object Name -match '^\d{4}-\d\d-\d\d_\d{6}$' | Remove-Item -Recurse -Force
}
Write-Output ''
Write-Output "$script:Pass passed, $script:Fail failed"
