# Downloads the third-party pieces the build and tests need, into tools\. None of them go in the repository.
#   powershell -ExecutionPolicy Bypass -File tools\get_deps.ps1
# Dear Esther is a 32-bit game, so every native piece is the x86 / Win32 build.
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$dl = Join-Path $tools 'dl'
New-Item -ItemType Directory -Force $dl | Out-Null
function Fetch([string]$Url, [string]$File) {
    $path = Join-Path $dl $File
    if (-not (Test-Path $path)) { Write-Host "Downloading $Url"; Invoke-WebRequest $Url -OutFile $path -UseBasicParsing }
    $path
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Extract([string]$Zip, [string]$Entry, [string]$To) {
    $z = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $e = $z.Entries | Where-Object { $_.FullName -eq $Entry }
        if (-not $e) { throw "$Entry is not in $Zip" }
        New-Item -ItemType Directory -Force (Split-Path $To -Parent) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $To, $true)
    } finally { $z.Dispose() }
}

# Unity Doorstop 4.3.0, the only loader the mod uses.
$zip = Fetch 'https://github.com/NeighTools/UnityDoorstop/releases/download/v4.3.0/doorstop_win_release_4.3.0.zip' 'doorstop_win_release_4.3.0.zip'
Extract $zip 'x86/winhttp.dll' "$tools\doorstop\winhttp.dll"
Extract $zip 'x86/.doorstop_version' "$tools\doorstop\.doorstop_version"

# Khronos OpenXR loader 1.1.65 (headers, Win32 import library and DLL).
$zip = Fetch 'https://www.nuget.org/api/v2/package/OpenXR.Loader/1.1.65' 'openxr.loader.1.1.65.nupkg'
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($e in $z.Entries) {
        if ($e.FullName -like 'include/openxr/*' -or $e.FullName -like 'native/Win32/release/*') {
            if ($e.Name -eq '') { continue }
            $to = Join-Path "$tools\openxr" $e.FullName
            New-Item -ItemType Directory -Force (Split-Path $to -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $to, $true)
        }
    }
} finally { $z.Dispose() }

# Valve OpenVR 2.15.6 (header, Win32 import library and DLL), for SteamVR.
$base = 'https://raw.githubusercontent.com/ValveSoftware/openvr/v2.15.6'
New-Item -ItemType Directory -Force "$tools\openvr\include", "$tools\openvr\win32" | Out-Null
Copy-Item (Fetch "$base/headers/openvr.h" 'openvr.h') "$tools\openvr\include\openvr.h" -Force
Copy-Item (Fetch "$base/lib/win32/openvr_api.lib" 'openvr_api.lib') "$tools\openvr\win32\openvr_api.lib" -Force
Copy-Item (Fetch "$base/bin/win32/openvr_api.dll" 'openvr_api.dll') "$tools\openvr\win32\openvr_api.dll" -Force


# ILSpy's command line, for reading the game's code.
if (-not (Test-Path "$tools\ilspy\ilspycmd.exe")) { dotnet tool install ilspycmd --version 8.2.0.7535 --tool-path "$tools\ilspy" }
Write-Host 'Done.'
