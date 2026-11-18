# Measures distant texture sharpness in two chapters at each Texture Sharpness (mip bias) value.
#   powershell -ExecutionPolicy Bypass -File tests\texture_quality.ps1
. "$PSScriptRoot\lab.ps1"
. "$PSScriptRoot\images.ps1"
Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
Enable-Lab
Set-DevrKey 'OpenXR' 'Runtime' "$Root\tools\mock_runtime.json"
$savedCfg = Get-Content $Cfg -Raw
Set-DevrKey 'Rendering' 'TextureSharpness' '0'
Set-Head
foreach ($f in 'cmd.txt', 'out.txt') { if (Test-Path "$Lab\$f") { [IO.File]::Delete("$Lab\$f") } }
Start-Devr
[void](Wait-Lab 120)
for ($i = 0; $i -lt 60 -and (Lab 'state' 1) -notmatch 'Main Menu'; $i++) { }
$results = @()
foreach ($lvl in 'Jakobson', 'Donnelley') {
    Lab "load $lvl" 1 | Out-Null
    for ($i = 0; $i -lt 40 -and (Lab 'state' 1) -notmatch "level=$lvl.*player=FPSCamera"; $i++) { }
    Start-Sleep 6
    Lab 'ts 0' | Out-Null; Start-Sleep 1
    "=== $lvl"; Lab 'textures' 2
    $runs = [ordered]@{ 'mip bias 0 (game)' = 'set TextureSharpness 0'; 'mip bias -0.5 (default)' = 'set TextureSharpness -0.5'; 'mip bias -1' = 'set TextureSharpness -1'; 'mip bias -1.5' = 'set TextureSharpness -1.5' }
    foreach ($name in $runs.Keys) {
        foreach ($c in $runs[$name]) { Lab $c 0.4 | Out-Null }
        Start-Sleep 6
        $n = "sh_${lvl}_" + ($name -replace '[^a-zA-Z0-9]', '')
        [void](Get-EyeDump $n 900)
        $img = [DevrBmp]::Load("$Lab\$n.L.bmp")
        # Distance band: just below the horizon (rows 900-1150); mid band 1150-1500; near 1700-2200.
        $far = [DevrBmp]::Sharpness($img, 900, 1150, 300, 1800); $mid = [DevrBmp]::Sharpness($img, 1150, 1500, 300, 1800); $near = [DevrBmp]::Sharpness($img, 1700, 2200, 300, 1800)
        $fps = [regex]::Match((Lab 'vr' 0.6), 'fps=([\d.]+)').Groups[1].Value
        "{0,-32} far {1:F3}  mid {2:F3}  near {3:F3}  fps {4}" -f $name, $far, $mid, $near, $fps
    }
    Lab 'ts 1' | Out-Null
}
Stop-Devr
Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline

