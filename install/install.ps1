# Installs or removes Dear Esther VR. Run through "Install Dear Esther VR.bat".
param(
    [ValidateSet('Install', 'Uninstall', 'Ask')][string]$Action = 'Ask',
    [string]$GameDir = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$exe = 'DearEsther.exe'

# The library with Steam's app manifest is the real install. A leftover copy of the game folder in another
# library would otherwise be picked, and the mod would never load.
function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
    $libraries = @($steam.Replace('/', '\'))
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value.Replace('\\', '\') }
    }
    $fallback = ''
    foreach ($l in ($libraries | Select-Object -Unique)) {
        $manifest = Join-Path $l 'steamapps\appmanifest_520720.acf'
        $dir = 'Dear Esther Landmark Edition'
        if (Test-Path $manifest) {
            $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $dir = $m.Groups[1].Value }
            $p = Join-Path $l "steamapps\common\$dir"
            if (Test-Path (Join-Path $p $exe)) { return $p }
        }
        $p = Join-Path $l "steamapps\common\$dir"
        if (-not $fallback -and (Test-Path (Join-Path $p $exe))) { $fallback = $p }
    }
    return $fallback
}

function Version-Of([string]$Folder) {
    $f = Join-Path $Folder 'version.txt'
    if (Test-Path $f) { (Get-Content $f -TotalCount 1).Trim() } else { '' }
}

Write-Host 'Dear Esther VR, for Dear Esther: Landmark Edition' -ForegroundColor Cyan
Write-Host ''
if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir $exe))) {
    Write-Host 'Could not find Dear Esther: Landmark Edition in your Steam libraries.' -ForegroundColor Red
    $GameDir = Read-Host "Paste the game folder path (the one containing `"$exe`")"
    if (-not (Test-Path (Join-Path $GameDir $exe))) { throw "$exe is not in: $GameDir" }
}
$package = Version-Of (Join-Path $here 'payload\DearEstherVR')
$installed = Version-Of (Join-Path $GameDir 'DearEstherVR')
Write-Host 'Game folder:       ' -NoNewline -ForegroundColor Gray; Write-Host $GameDir -ForegroundColor White
Write-Host 'Installed version: ' -NoNewline -ForegroundColor Gray; Write-Host $(if ($installed) { $installed } else { 'none' }) -ForegroundColor White
Write-Host 'This package:      ' -NoNewline -ForegroundColor Gray; Write-Host $package -ForegroundColor White
Write-Host ''
if (Get-Process 'DearEsther' -ErrorAction SilentlyContinue) { throw 'Please close Dear Esther first.' }

if ($Action -eq 'Ask') {
    $c = Read-Host 'Type 1 to install Dear Esther VR, or 2 to uninstall it'
    Write-Host ''
    $Action = switch ($c) { '1' { 'Install' } '2' { 'Uninstall' } default { throw 'Nothing was changed.' } }
}

# Another loader's winhttp.dll (BepInEx, for example) would be replaced, which would switch that loader off.
$proxy = Join-Path $GameDir 'winhttp.dll'
$ourConfig = Join-Path $GameDir 'doorstop_config.ini'
$ours = (Test-Path $ourConfig) -and ((Get-Content $ourConfig -Raw) -match 'DearEstherVR')

if ($Action -eq 'Install') {
    if ((Test-Path $proxy) -and -not $ours) {
        throw "Another mod loader is installed (winhttp.dll and doorstop_config.ini in the game folder). Dear Esther VR loads itself without one, so please remove the other loader first."
    }
    Copy-Item (Join-Path $here 'payload\*') $GameDir -Recurse -Force
    # Every file must have arrived intact.
    foreach ($f in Get-ChildItem (Join-Path $here 'payload') -Recurse -File -Force) {
        $rel = $f.FullName.Substring((Join-Path $here 'payload').Length + 1)
        $dest = Join-Path $GameDir $rel
        if (-not (Test-Path $dest) -or (Get-FileHash $dest).Hash -ne (Get-FileHash $f.FullName).Hash) { throw "Copying $rel failed. Is the game folder writable?" }
    }
    # The developer test mode must never be on in a player's install.
    $cfg = Join-Path $GameDir 'DearEstherVR\DearEstherVR.cfg'
    if (Test-Path $cfg) {
        $text = Get-Content $cfg
        $fixed = $text -replace '^LabCommands = true', 'LabCommands = false' -replace '^Skip = .*$', 'Skip = '
        if (($fixed -join "`n") -ne ($text -join "`n")) { Set-Content $cfg $fixed -Encoding ascii; Write-Host 'Switched off the developer test settings.'; Write-Host '' }
    }
    Write-Host "Dear Esther VR $package is installed and checked." -ForegroundColor Green
    Write-Host ''
    Write-Host 'Start SteamVR or Virtual Desktop, then start Dear Esther: Landmark Edition from Steam.' -ForegroundColor Yellow
    Write-Host 'The first line of DearEstherVR\DearEstherVR.log in the game folder shows the version that ran.' -ForegroundColor Gray
    return
}

# Uninstall. The game's own files, settings and saves are never touched.
foreach ($item in 'DearEstherVR', '.doorstop_version') {
    $p = Join-Path $GameDir $item
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
if ($ours) {
    Remove-Item $ourConfig -Force
    if (Test-Path $proxy) { Remove-Item $proxy -Force }
}
Write-Host 'Dear Esther VR is removed, and the game is back to its original files.' -ForegroundColor Green
