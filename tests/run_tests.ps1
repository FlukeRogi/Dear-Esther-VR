# End-to-end tests: drives the real game through the mock HMD (a 32-bit OpenXR runtime with a scripted head).
#   powershell -ExecutionPolicy Bypass -File tests\run_tests.ps1
# Steam Cloud is off for this game on the dev PC, and the user allows tests to use and change the save.
# The mod's settings are restored afterwards.
param([switch]$KeepOpen)
. "$PSScriptRoot\lab.ps1"
. "$PSScriptRoot\images.ps1"
$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++; Write-Output "PASS  $Name  $Detail" } else { $script:Fail++; Write-Output "FAIL  $Name  $Detail" }
}
function Val([string]$Text, [string]$Key) { [regex]::Match($Text, "\b$Key=([^\s]+)").Groups[1].Value }
function Num([string]$Text, [string]$Key) { $v = 0.0; if ([double]::TryParse((Val $Text $Key), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v)) { $v } else { [double]::NaN } }
function Vec([string]$Text, [string]$Key) { (Val $Text $Key).Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) } }
function Dist($A, $B) { [math]::Sqrt(($A[0] - $B[0]) * ($A[0] - $B[0]) + ($A[1] - $B[1]) * ($A[1] - $B[1]) + ($A[2] - $B[2]) * ($A[2] - $B[2])) }
function Near([double]$A, [double]$B, [double]$Tol = 0.02) { [math]::Abs($A - $B) -le $Tol }
function AngleDiff([double]$A, [double]$B) { $d = ($A - $B) % 360; if ($d -gt 180) { $d -= 360 }; if ($d -lt -180) { $d += 360 }; $d }
function Heading($From, $To) { [math]::Atan2($To[0] - $From[0], $To[2] - $From[2]) * 180 / [math]::PI }
function Settle([double]$Seconds = 1) { Start-Sleep -Milliseconds ([int]($Seconds * 1000)) }
function Layers { Get-Content "$Lab\mock_layers.txt" }
function Load([string]$Name) { [DevrBmp]::Load("$Lab\$Name") }
function Crashed { (Select-String -Path "$Game\DearEsther_Data\output_log.txt" -Pattern 'Crash!!!' -ErrorAction SilentlyContinue).Count -gt 0 }
$inv = [Globalization.CultureInfo]::InvariantCulture

# --- Setup ---------------------------------------------------------------------------------------
Stop-Devr
Get-Process DearEsther, WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
$savedCfg = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { $null }
Enable-Lab
Set-DevrKey 'OpenXR' 'Runtime' "$Root\tools\mock_runtime.json"
$defaults = [ordered]@{ 'View.WorldScale' = '1'; 'View.HeightMode' = 'standing'; 'View.HeightOffset' = '0'; 'View.HeadBob' = 'false'
    'Movement.WalkDirection' = 'head'; 'Movement.Turning' = 'smooth'; 'Movement.SnapAngle' = '30'
    'Rendering.ResolutionScale' = '1'; 'Rendering.TextureSharpness' = '-0.5'; 'Rendering.ShadowDepth' = '2'; 'Rendering.UnderwaterDistortion' = '0.25'; 'Rendering.Antialiasing' = '0'; 'Rendering.SymmetricEyes' = 'false'; 'Rendering.DoubleVision' = 'false'
    'Debug.Msaa' = '0'; 'Debug.DrawCopy' = 'true'; 'Debug.Skip' = '' }
foreach ($kv in $defaults.GetEnumerator()) { $p = $kv.Key.Split('.'); Set-DevrKey $p[0] $p[1] $kv.Value }
Set-Head
foreach ($f in 'mock_log.txt', 'mock_dump.txt', 'mock_exit.txt', 'cmd.txt', 'out.txt') { if (Test-Path "$Lab\$f") { [IO.File]::Delete("$Lab\$f") } }
$t0 = Get-Date
# Started as a player would: fullscreen, as the game's own settings say.
# The mock starts like VDXR did: its first frames put the head on the floor, flagged as tracked.
'20' | Set-Content "$Lab\mock_floor_frames.txt" -Encoding ascii
Start-Devr
[void](Wait-Lab 120)
for ($i = 0; $i -lt 60 -and (Lab 'state' 1) -notmatch 'level=Main_Menu|level=Main Menu'; $i++) { }
$startup = ((Get-Date) - $t0).TotalSeconds
Settle 3

# --- Start-up and session -------------------------------------------------------------------------
$log = Get-Content "$ModDir\DearEstherVR.log" -Raw
Check 'Doorstop loads the mod, no other loader' ($log -match 'Doorstop entry point reached' -and -not (Test-Path "$Game\BepInEx")) ''
Check 'The game reaches its main menu' ((Lab 'state') -match 'Main Menu|Main_Menu') ("{0:F0} s after launch" -f $startup)
Check 'No errors in the mod log' ($log -notmatch '\[Error\]') ''
Check 'The game window left fullscreen for VR' ((Lab 'render') -match 'fullscreen=False') ''
$vr = Lab 'vr'
Check 'VR session reaches FOCUSED' ((Val $vr 'active') -eq 'True' -and (Val $vr 'state') -eq '5') "state=$(Val $vr 'state')"
$a = Num $vr 'submitted'; Settle 2; $b = Num (Lab 'vr') 'submitted'
Check 'Frames keep being submitted' ($b -gt $a + 60) "$a -> $b"
Check 'Eyes render with the game''s own MSAA' ((Val $vr 'msaa') -eq '4') "msaa=$(Val $vr 'msaa')"

# --- Resolution changes (Unity 5.3 swaps render textures; the bridge must survive it) --------------
foreach ($r in '1600 900 0', '1920 1080 1', '1280 720 0') { Lab "res $r" 1 | Out-Null; Settle 3 }
$a = Num (Lab 'vr') 'submitted'; Settle 1; $b = Num (Lab 'vr') 'submitted'
Check 'Resolution and fullscreen changes during VR: no crash, frames continue' (-not (Crashed) -and $b -gt $a + 30) "$a -> $b"

# --- Menu panel -----------------------------------------------------------------------------------
$vr = Lab 'vr'
Check 'The main menu counts as a menu' ((Val $vr 'menu') -eq 'True') ''
[void](Get-EyeDump 't_menu')
$cov = [DevrBmp]::Coverage((Load 't_menu.ui.bmp'), 10)
Check 'The main menu shows on the interface panel' ($cov -gt 0.2) ("coverage={0:F2}" -f $cov)
Remove-Item "$Lab\mock_floor_frames.txt" -ErrorAction SilentlyContinue
$log = Get-Content "$ModDir\DearEstherVR.log" -Raw
$recenters = [regex]::Matches($log, 'head ([\d.]+) m above the floor') | ForEach-Object { [double]::Parse($_.Groups[1].Value, $inv) }
Check 'Start-up never recenters on the placeholder pose lying on the floor' ($recenters.Count -gt 0 -and ($recenters | Where-Object { $_ -lt 0.3 }).Count -eq 0) ("recenters at " + (($recenters | ForEach-Object { $_.ToString('F2', $inv) }) -join ', ') + " m")
$vr = Lab 'vr'
Check 'The logo and menu panel opens at eye level' (Near ((Vec $vr 'panel')[1]) 0 0.01) ("panel y {0:F3}, head y 0" -f (Vec $vr 'panel')[1])
# Recenter (F9) with the head somewhere else brings the panel to it, even while a menu is open.
Set-Head -Y 0.3 -Yaw 40; Settle 0.5
Lab 'recenter' 1 | Out-Null
$vr = Lab 'vr'; $pp = Vec $vr 'panel'
Check 'Recenter brings an open menu''s panel to eye level, straight ahead' ((Near $pp[1] 0.3 0.01) -and [math]::Abs((AngleDiff (Num $vr 'panelYaw') -40)) -lt 1) ("panel y {0:F3} yaw {1}" -f $pp[1], (Val $vr 'panelYaw'))
Set-Head; Settle 0.5; Lab 'recenter' 1 | Out-Null
Save-Flat "$Lab\t_menu.ui.bmp" "$Lab\t_menu_ui.png"

# --- VR options page in the game's own Options menu ---------------------------------------------
Lab 'click Options Button' 1 | Out-Null
$r = Lab 'click VR Button' 1.5
Check 'Options has a VR entry that opens the VR page' ($r -match 'submitted .*Options Panel/VR Button' -and (Lab 'selected') -match 'MenuVRPanel/Recenter') ''
$info = Lab 'vrinfo'
Check 'VR page rows show the current settings' ($info -match 'Height=Standing' -and $info -match 'Walk_Direction=Head' -and $info -match 'Turning=Smooth' -and $info -match 'Resolution=100%') ($info.Trim() -replace '^> vrinfo\s*', '')
Lab 'vrrow Height Seated' 0.5 | Out-Null; Lab 'vrrow World_Scale 1.2x' 0.5 | Out-Null; Settle 1
$cfgNow = Lab 'cfg'
Check 'Changing a VR row changes the setting at once' ($cfgNow -match 'View.HeightMode=seated' -and $cfgNow -match 'View.WorldScale=1.2') ''
Lab 'vrrow Height Standing' 0.5 | Out-Null; Lab 'vrrow World_Scale 1.0x' 0.5 | Out-Null; Settle 1
$nav = Lab 'nav down' 0.8
Check 'Gamepad down moves from Recenter to the first setting' ($nav -match 'MenuVRPanel/VR Height') ''
$back = Lab 'nav cancel' 1
Check 'Back leaves the VR page to the VR entry' ($back -match 'Options Panel/VR Button') ''
Check 'VR page labels survive the game''s localiser' ((Lab 'tree MenuVRPanel') -notmatch 'NotInStringTable') ''
Lab 'nav cancel' 1 | Out-Null

# --- Chapter 1 ------------------------------------------------------------------------------------
$before = (Get-Item "$Lab\out.txt").Length
Lab 'commentary 1' | Out-Null
'load Donnelley' | Set-Content "$Lab\cmd.txt" -Encoding ascii
for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Milliseconds 300; 'fade' | Set-Content "$Lab\cmd.txt" -Encoding ascii }
Start-Sleep 1
$fs = [IO.File]::Open("$Lab\out.txt", 'Open', 'Read', 'ReadWrite'); [void]$fs.Seek($before, 'Begin'); $r = New-Object IO.StreamReader $fs; $loadOut = $r.ReadToEnd(); $r.Close()
Check 'Loading a chapter turns both eyes black (no loading card on the panel)' ($loadOut -match 'fade=0.00,0.00,0.00,1.000 source=level_load') ''
for ($i = 0; $i -lt 40 -and (Lab 'state' 1) -notmatch 'player=FPSCamera'; $i++) { }
Settle 6
$st = Lab 'state'
Check 'Chapter 1 loads with the player camera' ($st -match 'chapter=True' -and $st -match 'player=FPSCamera') ''
Check 'Play is not a menu' ((Val (Lab 'vr') 'menu') -eq 'False') ''
Check 'The pause menu''s Options also has the VR page' ((Lab 'find VrPanel') -match 'In-Game Menu/Menu Panel/Options Menu/MenuVRPanel') ''
Settle 5
$ic = Lab 'icons DE_icons' 2
Check 'Developer commentary icons stay upright (vertical billboards) in VR' ($ic -match 'VO DC Trigger' -and $ic -match 'mode=VerticalBillboard' -and $ic -notmatch 'mode=Billboard\)') ''
Check 'The sea renders reflections every frame (no alternate-frame updates)' ((Lab 'water') -match 'tick=False' -and (Lab 'water') -notmatch 'tick=True') (Lab 'water').Trim()
[void](Get-EyeDump 't_play')
$layers = Layers
Check 'World layer behind the interface quad' ($layers[0] -match 'layers=2' -and $layers[1] -match '^projection view=0' -and $layers[3] -match '^quad') ($layers[0])
Check 'No OpenXR API misuse reported by the mock' ((Val $layers[0] 'bad') -eq '0') ($layers[0])
$fovL = (Val $layers[1] 'fov').Split(',') | ForEach-Object { [double]::Parse($_, $inv) }
$fovR = (Val $layers[2] 'fov').Split(',') | ForEach-Object { [double]::Parse($_, $inv) }
Check 'Each eye renders exactly the headset''s own asymmetric view' ((Near $fovL[0] -0.90 0.001) -and (Near $fovL[1] 0.78 0.001) -and (Near $fovR[0] -0.78 0.001) -and (Near $fovR[1] 0.90 0.001) -and (Near $fovL[2] 0.85 0.001) -and (Near $fovL[3] -0.95 0.001)) ("L=" + ($fovL -join ',') + " R=" + ($fovR -join ','))
Check 'Eye images match the recommended size' ((Val $layers[1] 'chain') -eq '2064x2272') (Val $layers[1] 'chain')
Check 'sRGB swapchains (the game renders in gamma space)' ((Val $layers[1] 'format') -eq '29' -and (Val $layers[3] 'format') -eq '29') ''
$cov = [DevrBmp]::Coverage((Load 't_play.ui.bmp'), 10)
Check 'During play the interface panel is see-through' ($cov -lt 0.02) ("coverage={0:F3}" -f $cov)

# --- Per-eye rendering ----------------------------------------------------------------------------
Lab 'ts 0' | Out-Null
Settle 1
$e1 = Vec (Lab 'vr') 'eye0'; Settle 2; $e2 = Vec (Lab 'vr') 'eye0'
Check 'Paused game: the view stays put (no head pose added twice)' ((Dist $e1 $e2) -lt 0.001) ("moved {0:F4}" -f (Dist $e1 $e2))
# With no distance between the eyes, both see the same scene from the same point. Their views are mirror images
# (the left eye sees further left), so they are compared direction by direction where they overlap. Any effect
# that carries state from one eye's render into the other's (water, refraction grabs, shadows) shows up here.
$tanL = @([math]::Tan(-0.90), [math]::Tan(0.78), [math]::Tan(0.85), [math]::Tan(-0.95))
$tanR = @([math]::Tan(-0.78), [math]::Tan(0.90), [math]::Tan(0.85), [math]::Tan(-0.95))
Set-Head -Ipd 0; Settle 1.5; [void](Get-EyeDump 't_ipd0')
$L0 = Load 't_ipd0.L.bmp'; $R0 = Load 't_ipd0.R.bmp'
$same = [DevrBmp]::ProjectionDiff($L0, $R0, $tanL, $tanR, 4)
$yard = [DevrBmp]::ProjectionDiffShifted($L0, $R0, $tanL, $tanR, 4, 3)
Set-Head; Settle 1.5; [void](Get-EyeDump 't_ipd')
$L = Load 't_ipd.L.bmp'; $R = Load 't_ipd.R.bmp'
$stereo = [DevrBmp]::ProjectionDiff($L, $R, $tanL, $tanR, 4)
Check 'Zero eye distance: both eyes see the same thing in every shared direction' ($same -lt $yard * 0.4) ("diff={0:F2}, same images 3 px apart: {1:F2}" -f $same, $yard)
Check 'Normal eye distance: the eyes see different images' ($stereo -gt $same * 1.5) ("diff={0:F2}" -f $stereo)
Lab 'fadetest 0.5' | Out-Null; Settle 1; [void](Get-EyeDump 't_fade')
Lab 'fadetest off' | Out-Null; Settle 0.5
$fl = [DevrBmp]::Mean((Load 't_fade.L.bmp'), 8); $fr = [DevrBmp]::Mean((Load 't_fade.R.bmp'), 8); $nl = [DevrBmp]::Mean($L, 8)
Check 'A half fade (as the game''s fades draw) darkens both eyes by half' ((Near $fl ($nl / 2) 2) -and (Near $fr ($nl / 2) 3)) ("mean {0:F1} -> L {1:F1} R {2:F1}" -f $nl, $fl, $fr)
$meanL = [DevrBmp]::Mean($L, 8); $meanR = [DevrBmp]::Mean($R, 8)
Check 'Both eyes have the same exposure and grading' ([math]::Abs($meanL - $meanR) -lt 2) ("mean L={0:F1} R={1:F1}" -f $meanL, $meanR)
$d = @(); foreach ($row in 600, 1100, 1600, 2000) { $d += [DevrBmp]::DirDisparity($L, $R, $tanL, $tanR, $row, 40, 60) }
Check 'Disparity is never negative (nothing appears beyond infinity)' (($d | Where-Object { $_ -lt 0 }).Count -eq 0) ("rows 600/1100/1600/2000: " + ($d -join ','))
Check 'Near ground has more disparity than the sky' ($d[3] -gt $d[0] -and $d[0] -le 1) ''

# Strict: both eyes from the same point with identical frustums must match pixel for pixel. Anything random
# per render (noise, grain, dithering) or carried between the eye renders would show here.
Lab 'set SymmetricEyes true' | Out-Null; Set-Head -Ipd 0; Settle 4; [void](Get-EyeDump 't_same')
$sameExact = [DevrBmp]::Diff((Load 't_same.L.bmp'), (Load 't_same.R.bmp'), 1)
Settle 1; [void](Get-EyeDump 't_same2')
$again = [DevrBmp]::Diff((Load 't_same.L.bmp'), (Load 't_same2.L.bmp'), 1)
Lab 'set SymmetricEyes false' | Out-Null; Set-Head; Settle 3
# Overlapping see-through grass cards at equal distances can swap draw order between renders: a few hundred
# pixels by a few levels. Anything that really differed per eye (noise, grain, effects) would be far larger.
$where = [DevrBmp]::DiffWhere((Load 't_same.L.bmp'), (Load 't_same.R.bmp'))
Check 'Same viewpoint, same frustum: the two eyes match (apart from grass draw order)' ($sameExact -lt 0.002 -and ($where -eq 'identical' -or [int]([regex]::Match($where, 'max (\d+)').Groups[1].Value) -le 16)) ("diff={0:F4}, {1}" -f $sameExact, $where)
Check 'A frozen frame renders the same twice (nothing random per render)' ($again -lt 0.002) ("diff={0:F4}" -f $again)

# --- Shadow Depth -----------------------------------------------------------------------------------
Lab 'set ShadowDepth 0' | Out-Null; Settle 1; [void](Get-EyeDump 't_sd0')
Lab 'set ShadowDepth 2' | Out-Null; Settle 1; [void](Get-EyeDump 't_sd2')
$s0 = Load 't_sd0.L.bmp'; $s2 = Load 't_sd2.L.bmp'
Check 'Shadow Depth darkens the dark parts' ([DevrBmp]::Pct($s2, 0.1) -lt [DevrBmp]::Pct($s0, 0.1) * 0.8 -and [DevrBmp]::Pct($s2, 0.5) -lt [DevrBmp]::Pct($s0, 0.5)) ("10th pct {0} -> {1}, median {2} -> {3}" -f [DevrBmp]::Pct($s0, 0.1), [DevrBmp]::Pct($s2, 0.1), [DevrBmp]::Pct($s0, 0.5), [DevrBmp]::Pct($s2, 0.5))
Check 'Shadow Depth leaves the bright parts as they were' ([math]::Abs([DevrBmp]::Pct($s2, 0.99) - [DevrBmp]::Pct($s0, 0.99)) -le 1 -and [DevrBmp]::Pct($s2, 1) -eq [DevrBmp]::Pct($s0, 1)) ("99th pct {0} -> {1}, brightest {2} -> {3}" -f [DevrBmp]::Pct($s0, 0.99), [DevrBmp]::Pct($s2, 0.99), [DevrBmp]::Pct($s0, 1), [DevrBmp]::Pct($s2, 1))

# --- Image orientation ----------------------------------------------------------------------------
Set-Head; Settle 1; [void](Get-EyeDump 't_up0')
Set-Head -Pitch 4; Settle 1.5; [void](Get-EyeDump 't_up1')
$vs = [DevrBmp]::VShift((Load 't_up0.L.bmp'), (Load 't_up1.L.bmp'), 90)
Check 'Images are upright (looking up moves the scenery down)' ($vs -gt 40) "shift=$vs px"
Set-Head

# --- Projection accuracy --------------------------------------------------------------------------
# The same eye, rendered once with the headset's exact asymmetric frustum and once with a wider symmetric one,
# must agree at every view direction.
[void](Get-EyeDump 't_asym')
Lab 'set SymmetricEyes true' | Out-Null; Settle 4; [void](Get-EyeDump 't_sym')
$symLayers = Layers
Lab 'set SymmetricEyes false' | Out-Null; Settle 3
$ta = @([math]::Tan(-0.90), [math]::Tan(0.78), [math]::Tan(0.85), [math]::Tan(-0.95))
$h = [math]::Tan(0.90); $v = [math]::Tan(0.95); $ts = @(-$h, $h, $v, -$v)
$asymImg = Load 't_asym.L.bmp'; $symImg = Load 't_sym.L.bmp'
$pd = [DevrBmp]::ProjectionDiff($asymImg, $symImg, $ta, $ts, 4)
$pd3 = [DevrBmp]::ProjectionDiffShifted($asymImg, $symImg, $ta, $ts, 4, 3)
Check 'Symmetric comparison render really was wider' ((Val $symLayers[1] 'fov') -match '^-0\.9000,0\.9000,0\.9500,-0\.9500') (Val $symLayers[1] 'fov')
Check 'Asymmetric eye matches the symmetric render direction for direction' ($pd -lt $pd3 * 0.6) ("diff={0:F2}, same images 3 px apart: {1:F2}" -f $pd, $pd3)

# --- World scale and head tracking -----------------------------------------------------------------
$vr0 = Lab 'vr'; $e0 = Vec $vr0 'eye0'; $e1 = Vec $vr0 'eye1'
Check 'Eye separation in the world matches the headset' (Near (Dist $e0 $e1) 0.063 0.002) ("{0:F4}" -f (Dist $e0 $e1))
Lab 'set WorldScale 2' | Out-Null; Settle 1
$vr2 = Lab 'vr'
Check 'World Scale 2 halves the eye separation' (Near (Dist (Vec $vr2 'eye0') (Vec $vr2 'eye1')) 0.0315 0.002) ("{0:F4}" -f (Dist (Vec $vr2 'eye0') (Vec $vr2 'eye1')))
Lab 'set WorldScale 1' | Out-Null; Settle 1
$bodyYaw = Num $vr0 'eyeYaw'
Set-Head -X 0.3; Settle 1
$m = Vec (Lab 'vr') 'eye0'
$moved = Dist $e0 $m
$dir = Heading $e0 $m
Check 'Head moving 0.3 m right moves the eyes 0.3 m to the body''s right' ((Near $moved 0.3 0.01) -and [math]::Abs((AngleDiff $dir ($bodyYaw + 90))) -lt 3) ("moved {0:F3} heading {1:F1}, body {2:F1}" -f $moved, $dir, $bodyYaw)
Set-Head -Yaw 30; Settle 1
$y = Num (Lab 'vr') 'eyeYaw'
Check 'Head turning 30 degrees left turns the view 30 degrees left' ([math]::Abs((AngleDiff $y ($bodyYaw - 30))) -lt 1) ("eyeYaw {0:F1}, body {1:F1}" -f $y, $bodyYaw)
Set-Head -Pitch 20; Settle 1
$p = Num (Lab 'vr') 'eyePitch'
Check 'Head pitching 20 degrees up pitches the view up' ([math]::Abs((AngleDiff $p -20)) -lt 1) ("eyePitch {0:F1}" -f $p)
Set-Head; Settle 1

# --- Height ------------------------------------------------------------------------------------------
$feet = Vec (Lab 'state') 'feet'; $eye = Vec (Lab 'vr') 'eye0'
Check 'Standing: eyes sit at the real head height above the game floor (mock floor 1.6 m down)' (Near ($eye[1] - $feet[1]) 1.6 0.01) ("{0:F3} m" -f ($eye[1] - $feet[1]))
Set-Head -Y 0.2; Settle 1
$eye = Vec (Lab 'vr') 'eye0'
Check 'Standing: rising 0.2 m raises the eyes 0.2 m' (Near ($eye[1] - $feet[1]) 1.8 0.01) ("{0:F3} m" -f ($eye[1] - $feet[1]))
Set-Head -Y 0.2; Settle 0.5; Lab 'set HeightMode seated' | Out-Null; Settle 1.5
$vr = Lab 'vr'; $eye = Vec $vr 'eye0'; $base = Vec $vr 'base'
Check 'Choosing seated recentres by itself, putting the eyes at the game''s own eye height' (Near $eye[1] $base[1] 0.01) ("eye {0:F3} base {1:F3} mode={2}" -f $eye[1], $base[1], (Val $vr 'height'))
Lab 'set HeightOffset 0.3' | Out-Null; Settle 1
$eye2 = Vec (Lab 'vr') 'eye0'
Check 'Height offset raises the eyes by that much' (Near ($eye2[1] - $eye[1]) 0.3 0.01) ("{0:F3}" -f ($eye2[1] - $eye[1]))
Lab 'set HeightOffset 0' | Out-Null; Lab 'set HeightMode standing' | Out-Null; Set-Head; Lab 'recenter' | Out-Null; Settle 1

# --- Movement and turning -------------------------------------------------------------------------------
Lab 'ts 1' | Out-Null; Settle 1
$startFeet = Vec (Lab 'state' 0.5) 'feet'
function Walk([double]$Seconds = 1.2) {
    # Every walk starts from the same spot on the quay.
    Lab ("tp {0} {1} {2}" -f $startFeet[0].ToString($inv), $startFeet[1].ToString($inv), $startFeet[2].ToString($inv)) 1 | Out-Null
    $a = Vec (Lab 'state' 0.5) 'feet'
    Lab "walk 0 1 $Seconds" ($Seconds + 1) | Out-Null
    $b = Vec (Lab 'state' 0.5) 'feet'
    @{ dist = [math]::Sqrt(($b[0] - $a[0]) * ($b[0] - $a[0]) + ($b[2] - $a[2]) * ($b[2] - $a[2])); heading = (Heading $a $b) }
}
$body = Num (Lab 'vr') 'eyeYaw'
Set-Head -Yaw 60; Settle 1
$w = Walk
Check 'Walk direction head: forward walks where the head looks' ($w.dist -gt 0.3 -and [math]::Abs((AngleDiff $w.heading ($body - 60))) -lt 12) ("walked {0:F2} m heading {1:F1}, head {2:F1}" -f $w.dist, $w.heading, ($body - 60))
Lab 'set WalkDirection body' | Out-Null; Settle 0.5
$body = Num (Lab 'vr') 'eyeYaw'; $body = $body + 60
$w = Walk
Check 'Walk direction body: forward walks along the body, wherever the head looks' ($w.dist -gt 0.3 -and [math]::Abs((AngleDiff $w.heading $body)) -lt 12) ("walked {0:F2} m heading {1:F1}, body {2:F1}" -f $w.dist, $w.heading, $body)
Lab 'set WalkDirection head' | Out-Null; Set-Head; Settle 1
$pad = Num (Lab 'state') 'padYaw'
Check 'The game''s own stick turning speed is left as its options set it' ($pad -gt 0.5) "padYaw=$pad"
Lab 'set Turning snap' | Out-Null; Settle 0.5
$y0 = Num (Lab 'state') 'yaw'; Settle 1; $y0b = Num (Lab 'state') 'yaw'
Check 'Snap turning: no drift with the stick at rest' ($y0 -eq $y0b) ("{0:F2} -> {1:F2}" -f $y0, $y0b)
$y0 = Num (Lab 'state') 'yaw'
Lab 'stick 1' 1 | Out-Null; Lab 'stick off' 0.5 | Out-Null; $y1 = Num (Lab 'state') 'yaw'
Lab 'stick -1' 1 | Out-Null; Lab 'stick off' 0.5 | Out-Null; Lab 'stick -1' 1 | Out-Null; Lab 'stick off' 0.5 | Out-Null; $y2 = Num (Lab 'state') 'yaw'
Check 'Snap turning: one push turns exactly the snap angle' (Near (AngleDiff $y1 $y0) 30 0.01) ("{0:F1} -> {1:F1}" -f $y0, $y1)
Check 'Snap turning: two pushes the other way turn back past the start' (Near (AngleDiff $y2 $y0) -30 0.01) ("{0:F1}" -f $y2)
Lab 'set Turning smooth' | Out-Null
$st = Lab 'state'
Check 'The body never pitches in VR (the headset supplies pitch)' ((Num $st 'pitch') -eq 0) "pitch=$(Val $st 'pitch')"

# --- Performance on this PC ---------------------------------------------------------------------------
$vr = Lab 'vr'
Check 'Keeps the mock headset''s 90 Hz in chapter 1' ((Num $vr 'fps') -gt 85) ("fps={0} modMs={1}" -f (Val $vr 'fps'), (Val $vr 'modMs'))
Lab 'set Antialiasing 8' | Out-Null; Settle 4
$vr = Lab 'vr'
# 8x MSAA costs frame rate on a slower GPU, so this only checks it switches and stays playable.
Check 'Anti-aliasing 8x switches live' ((Val $vr 'msaa') -eq '8' -and (Num $vr 'fps') -gt 70 -and -not (Crashed)) ("msaa={0} fps={1}" -f (Val $vr 'msaa'), (Val $vr 'fps'))
Lab 'set Antialiasing 0' | Out-Null; Settle 3

# --- The moon (Paul's chapter) ------------------------------------------------------------------------
Lab 'load Paul' 1 | Out-Null
for ($i = 0; $i -lt 40 -and (Lab 'state' 1) -notmatch 'level=Paul'; $i++) { }
Settle 5
Set-Head -Yaw 70 -Pitch 15; Settle 1.5
$moon = Lab 'moon'
$ang = [double]::Parse([regex]::Match($moon, 'angleToEye=([\d.]+)').Groups[1].Value, $inv)
Check 'The moon faces the eye with the head turned 70 degrees away' ($ang -lt 0.5) ($moon -replace '(?s)^> moon\s*', '').Trim()
Set-Head

# --- Underwater (Esther's chapter) -------------------------------------------------------------------
Lab 'load Esther' 1 | Out-Null
for ($i = 0; $i -lt 40 -and (Lab 'state' 1) -notmatch 'level=Esther'; $i++) { }
for ($i = 0; $i -lt 60 -and (Lab 'refract' 1) -match 'no active'; $i++) { Start-Sleep 1 }
Lab 'ts 0' | Out-Null; Settle 1; [void](Get-EyeDump 't_uw0')
Lab 'refract 1' | Out-Null; Settle 2; [void](Get-EyeDump 't_uw1')
$e = 0.0; $zoom = [DevrBmp]::ScaleMatch((Load 't_uw0.L.bmp'), (Load 't_uw1.L.bmp'), [ref]$e)
Check 'Underwater at the default strength: at most a 4% zoom (the game''s own is 15%)' ($zoom -le 1.04) ("zoom {0:F3}" -f $zoom)
Lab 'set UnderwaterDistortion 0' | Out-Null; Settle 2; [void](Get-EyeDump 't_uwoff')
$e2 = 0.0; $zoomOff = [DevrBmp]::ScaleMatch((Load 't_uw0.L.bmp'), (Load 't_uwoff.L.bmp'), [ref]$e2)
Check 'Underwater distortion Off: the normal view, no zoom' ($zoomOff -eq 1 -and [DevrBmp]::Diff((Load 't_uw0.L.bmp'), (Load 't_uwoff.L.bmp'), 2) -lt 0.1) ("zoom {0:F3}" -f $zoomOff)
Lab 'refract 0' | Out-Null; Lab 'set UnderwaterDistortion 0.25' | Out-Null; Lab 'ts 1' | Out-Null

# --- Leaving VR -------------------------------------------------------------------------------------
'' | Set-Content "$Lab\mock_exit.txt" -Encoding ascii
Settle 3
$vr = Lab 'vr'
Check 'The runtime ending the session returns the game to flat play' ((Val $vr 'active') -eq 'False' -and -not (Crashed)) "active=$(Val $vr 'active')"
$ic = Lab 'icons DE_icons'
Check 'Leaving VR puts the commentary icons back as the game made them' ($ic -match 'mode=Billboard\)' -and $ic -notmatch 'mode=VerticalBillboard') ("vr active=" + (Val (Lab 'vr') 'active'))
$cams = Lab 'cams'
Check 'The player camera renders to the screen again' ($cams -match 'FPSCamera enabled=True active=True') ''

$log = Get-Content "$ModDir\DearEstherVR.log" -Raw
Check 'Still no errors in the mod log' ($log -notmatch '\[Error\]') ''
Check 'The game never crashed' (-not (Crashed)) ''

# --- Teardown ---------------------------------------------------------------------------------------
if (-not $KeepOpen) { Stop-Devr }
Get-Process WerFault -ErrorAction SilentlyContinue | Stop-Process -Force
if ($null -ne $savedCfg) { Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline }
Get-ChildItem $Game -Directory | Where-Object Name -match '^\d{4}-\d\d-\d\d_\d{6}$' | Remove-Item -Recurse -Force
"$Pass passed, $Fail failed" | Tee-Object "$PSScriptRoot\last_run.txt"
