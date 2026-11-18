# Builds the release zip in dist\ from the release builds. Run after building the native and C# projects.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = [regex]::Match((Get-Content "$root\src\Mod.cs" -Raw), 'Version = "([^"]+)"').Groups[1].Value
$dist = Join-Path $root 'dist'
$staging = Join-Path $dist 'stage'
$stage = Join-Path $staging 'Dear Esther VR'
$zip = Join-Path $dist "Dear-Esther-VR-$version.zip"
foreach ($old in $staging, $zip) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
$payload = Join-Path $stage 'payload'
$mod = Join-Path $payload 'DearEstherVR'
New-Item -ItemType Directory -Force $mod | Out-Null
$need = @("$root\build\DearEstherVR.dll", "$root\native\build\Release\DEVRNative.dll", "$root\tools\openxr\native\Win32\release\bin\openxr_loader.dll",
          "$root\tools\openvr\win32\openvr_api.dll", "$root\tools\doorstop\winhttp.dll", "$root\tools\doorstop\.doorstop_version")
foreach ($f in $need) { if (-not (Test-Path $f)) { throw "Missing $f. Build first, and run tools\get_deps.ps1." } }
Copy-Item $need[0..3] $mod
Set-Content (Join-Path $mod 'version.txt') $version -Encoding ascii
Copy-Item $need[4..5] $payload
Copy-Item "$root\install\doorstop_config.ini" $payload
Copy-Item "$root\install\install.ps1", "$root\install\Install Dear Esther VR.bat", "$root\install\README.txt" $stage
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Get-Item $zip | Select-Object FullName, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
