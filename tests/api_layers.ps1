# A broken OpenXR implicit API layer, as players have hit: the loader fails xrCreateInstance with error -32
# (XR_ERROR_FILE_ACCESS_ERROR) for every runtime when such a layer can't load in a 32-bit game.
# Each case registers one test layer under HKCU\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit. The layer has an
# enable_environment, so it is inert for every program except the game started here with that variable set, and
# it is removed again at the end, even after a failure.
#   powershell -ExecutionPolicy Bypass -File tests\api_layers.ps1
. "$PSScriptRoot\lab.ps1"
$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++; Write-Output "PASS  $Name  $Detail" } else { $script:Fail++; Write-Output "FAIL  $Name  $Detail" }
}
$key = 'HKCU:\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit'
$createdKeys = @()
$manifest = "$Lab\devr_test_layer.json"
function Remove-TestLayer {
    if (Test-Path $key) {
        Remove-ItemProperty $key -Name $manifest -ErrorAction SilentlyContinue
    }
    # Keys this test created are removed again, deepest first, if nothing else was put in them meanwhile.
    for ($i = $createdKeys.Count - 1; $i -ge 0; $i--) {
        $k = $createdKeys[$i]
        if ((Test-Path $k) -and -not (Get-Item $k).Property -and -not (Get-ChildItem $k)) { Remove-Item $k }
    }
}
function Set-TestLayer([string]$Lib) {
    $json = '{ "file_format_version": "1.0.0", "api_layer": { "name": "XR_APILAYER_DEVR_test", "library_path": "' + ($Lib -replace '\\', '\\') +
            '", "api_version": "1.0", "implementation_version": "1", "description": "Dear Esther VR test layer", "disable_environment": "DEVR_TEST_LAYER_DISABLE", "enable_environment": "DEVR_TEST_LAYER" } }'
    Set-Content $manifest $json -Encoding ascii
    # Creates only the missing keys, one level at a time (New-Item -Force would empty an existing one).
    $path = 'HKCU:'
    foreach ($part in ($key -replace '^HKCU:\\', '') -split '\\') {
        $path = "$path\$part"
        if (-not (Test-Path $path)) { [void](New-Item $path); $script:createdKeys += $path }
    }
    New-ItemProperty $key -Name $manifest -PropertyType DWord -Value 0 -Force | Out-Null
}

Stop-Devr
Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
$savedCfg = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { $null }
Enable-Lab
Set-Head
$mock = "$Root\tools\mock_runtime.json"
$x64 = 'C:\Program Files (x86)\Steam\steamclient64.dll'
$cases = [ordered]@{
    'ApiLayers = on, 64-bit layer: the loader fails (-32)' = @{ Mode = 'on'; Lib = $x64; Active = $false; Expect = 'xrCreateInstance failed: -32' }
    'auto, 64-bit layer' = @{ Mode = 'auto'; Lib = $x64; Active = $true; Expect = 'switched off for this game, because its DLL is 64-bit only' }
    'auto, layer DLL uninstalled' = @{ Mode = 'auto'; Lib = "$Lab\no_such_layer.dll"; Active = $true; Expect = "switched off for this game, because its DLL doesn't exist" }
    'auto, layer the check passes but fails to load' = @{ Mode = 'auto'; Lib = 'devr_no_such_layer.dll'; Active = $true; Expect = 'trying again with every OpenXR API layer switched off' }
    'ApiLayers = off' = @{ Mode = 'off'; Lib = $x64; Active = $true; Expect = 'switched off for this game (ApiLayers = off)' }
}
try {
    if (-not (Test-Path $x64)) { throw "Needs a 64-bit DLL at $x64" }
    $env:DEVR_TEST_LAYER = '1'
    foreach ($name in $cases.Keys) {
        $c = $cases[$name]
        Set-TestLayer $c.Lib
        Set-DevrKey 'OpenXR' 'Runtime' $mock
        Set-DevrKey 'OpenXR' 'ApiLayers' $c.Mode
        foreach ($f in 'cmd.txt', 'out.txt') { if (Test-Path "$Lab\$f") { [IO.File]::Delete("$Lab\$f") } }
        Start-Devr -GameArgs @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720')
        [void](Wait-Lab 120)
        $vr = ''
        for ($i = 0; $i -lt 12; $i++) { $vr = Lab 'vr' 1; if ($vr -match 'active=True') { break } }
        $log = Get-Content "$ModDir\DearEstherVR.log" -Raw
        $lines = (([regex]::Matches($log, '\[(Info|Warn)\] (OpenXR API|Can''t|Connected|\[native\] xrCreate)[^\r\n]*') | ForEach-Object { $_.Value }) -join ' | ')
        Check "$name : logged" ($log -match [regex]::Escape($c.Expect)) $lines
        Check "$name : VR $(if ($c.Active) { 'starts' } else { 'does not start' })" (($vr -match 'active=True') -eq $c.Active) ''
        Stop-Devr
        Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}
finally {
    Stop-Devr
    Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item Env:\DEVR_TEST_LAYER -ErrorAction SilentlyContinue
    Remove-TestLayer
    if (Test-Path $manifest) { Remove-Item $manifest }
    if ($null -ne $savedCfg) { Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline }
}
Write-Output "Test layer removed: $(-not ((Test-Path $key) -and ((Get-Item $key).Property -contains $manifest)))"
Write-Output ''
Write-Output "$script:Pass passed, $script:Fail failed"
