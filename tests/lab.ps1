# Test helpers. Dot-source this file: . .\tests\lab.ps1
$script:Root = Split-Path $PSScriptRoot -Parent
$script:Game = 'C:\Program Files (x86)\Steam\steamapps\common\Dear Esther Landmark Edition'
$script:ModDir = "$Game\DearEstherVR"
$script:Lab = "$env:USERPROFILE\AppData\LocalLow\DearEstherVR\lab"
$script:Cfg = "$ModDir\DearEstherVR.cfg"
New-Item -ItemType Directory -Force $Lab | Out-Null

function Set-DevrKey([string]$Section, [string]$Key, [string]$Value) {
    # Replaces the key if present, otherwise adds it to its section.
    $t = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { '' }
    if ($t -match "(?m)^$Key = .*$") { $t = [regex]::Replace($t, "(?m)^$Key = .*$", ("$Key = " + $Value.Replace('$', '$$'))) }
    else { $t += "`r`n[$Section]`r`n$Key = $Value`r`n" }
    Set-Content $Cfg $t -Encoding ascii -NoNewline
}

function Enable-Lab { Set-DevrKey 'Debug' 'LabCommands' 'true' }
function Disable-Lab { Set-DevrKey 'Debug' 'LabCommands' 'false' }

function Lab([string]$Command, [double]$Wait = 1.5) {
    if (-not (Test-Path "$Lab\out.txt")) { '' | Set-Content "$Lab\out.txt" -Encoding ascii }
    $before = (Get-Item "$Lab\out.txt").Length
    $Command | Set-Content "$Lab\cmd.txt" -Encoding ascii
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
    $fs = [IO.File]::Open("$Lab\out.txt", 'Open', 'Read', 'ReadWrite')
    [void]$fs.Seek($before, 'Begin')
    $r = New-Object IO.StreamReader $fs
    $t = $r.ReadToEnd(); $r.Close(); $t
}

function Start-Devr([int]$Wait = 0, [string[]]$GameArgs = @()) {
    # Started directly, so Steam shows no launch dialog when SteamVR is already running.
    $env:SteamAppId = '520720'; $env:SteamGameId = '520720'
    if ($GameArgs.Count -gt 0) { Start-Process "$Game\DearEsther.exe" -WorkingDirectory $Game -ArgumentList $GameArgs } else { Start-Process "$Game\DearEsther.exe" -WorkingDirectory $Game }
    if ($Wait -gt 0) { Start-Sleep $Wait }
}

function Stop-Devr {
    if (Get-Process 'DearEsther' -ErrorAction SilentlyContinue) {
        'quit' | Set-Content "$Lab\cmd.txt" -Encoding ascii
        for ($i = 0; $i -lt 15 -and (Get-Process 'DearEsther' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
        Get-Process 'DearEsther' -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
        for ($i = 0; $i -lt 10 -and (Get-Process 'DearEsther' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
        Get-Process 'DearEsther' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 2
        if (Test-Path "$Lab\cmd.txt") { [IO.File]::Delete("$Lab\cmd.txt") }
    }
}

# Waits until the lab channel answers, which means the mod is running in the game.
function Wait-Lab([int]$Seconds = 120) {
    for ($i = 0; $i -lt $Seconds; $i += 2) {
        $r = Lab 'state' 2
        if ($r -match 'session=') { return $r }
    }
    throw 'The game did not answer on the lab channel'
}

function Set-Head([double]$X = 0, [double]$Y = 0, [double]$Z = 0, [double]$Yaw = 0, [double]$Ipd = 0.063, [double]$Pitch = 0) {
    $c = [Globalization.CultureInfo]::InvariantCulture
    $text = "{0} {1} {2} {3} {4} {5}" -f $X.ToString($c), $Y.ToString($c), $Z.ToString($c), $Yaw.ToString($c), $Ipd.ToString($c), $Pitch.ToString($c)
    # The mock HMD reads this file every frame, so it is swapped in whole, never half written.
    [IO.File]::WriteAllText("$Lab\mock_head.tmp", $text)
    for ($i = 0; $i -lt 50; $i++) {
        try {
            if (Test-Path "$Lab\mock_head.txt") { [IO.File]::Replace("$Lab\mock_head.tmp", "$Lab\mock_head.txt", [NullString]::Value) } else { [IO.File]::Move("$Lab\mock_head.tmp", "$Lab\mock_head.txt") }
            return
        } catch { Start-Sleep -Milliseconds 20 }
    }
    throw 'Could not update the mock head pose'
}

function Get-EyeDump([string]$Name, [int]$Width = 800) {
    # Asks the mock HMD for its next submitted frame, then saves a side-by-side PNG of both eyes and the UI.
    '' | Set-Content "$Lab\mock_dump.txt" -Encoding ascii
    for ($i = 0; $i -lt 40 -and (Test-Path "$Lab\mock_dump.txt"); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path "$Lab\mock_dump.txt") { throw 'The mock HMD did not produce a frame with layers' }
    Add-Type -AssemblyName System.Drawing
    $l = [Drawing.Image]::FromFile("$Lab\mock_left.bmp"); $r = [Drawing.Image]::FromFile("$Lab\mock_right.bmp")
    $h = [int]($Width * $l.Height / $l.Width)
    $b = New-Object Drawing.Bitmap ($Width * 2), $h
    $g = [Drawing.Graphics]::FromImage($b)
    $g.DrawImage($l, 0, 0, $Width, $h); $g.DrawImage($r, $Width, 0, $Width, $h)
    $b.Save("$Lab\$Name.png", [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose(); $l.Dispose(); $r.Dispose()
    Copy-Item "$Lab\mock_left.bmp" "$Lab\$Name.L.bmp" -Force; Copy-Item "$Lab\mock_right.bmp" "$Lab\$Name.R.bmp" -Force
    if (Test-Path "$Lab\mock_ui.bmp") { Copy-Item "$Lab\mock_ui.bmp" "$Lab\$Name.ui.bmp" -Force }
    "$Lab\$Name.png"
}

