# Builds a Kairn release to publish on GitHub.
#   .\publish.ps1            -> rebuilds the current version
#   .\publish.ps1 1.0.1      -> sets the version to 1.0.1, then builds
# Output: release\Kairn-Setup-<version>.exe (the setup, also used by automatic updates).

param([string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "Kairn\Kairn.csproj"

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Expected a version like 1.2.3" }
    # Read and write as UTF-8 without BOM, explicitly: otherwise PowerShell 5.1 reads the file
    # as ANSI and writes accented characters back double-encoded.
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $text = [System.IO.File]::ReadAllText($csproj, $utf8) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    [System.IO.File]::WriteAllText($csproj, $text, $utf8)
}
$Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$out = Join-Path $root "release"
$build = Join-Path $out "build"
# UNCOMPRESSED exe: a bigger download (~205 MB instead of ~95), but Windows then reads the libraries
# straight from the file instead of unpacking them in memory at every launch.
# Measured: half the RAM (115 MB instead of 245) and about 0.4 s saved at every start.
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o $build --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$setup = Join-Path $out "Kairn-Setup-$Version.exe"
Copy-Item (Join-Path $build "Kairn.exe") $setup -Force
Remove-Item $build -Recurse -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host "Ready: $setup" -ForegroundColor Green
Write-Host ("Size: {0:N0} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host "SHA-256: $hash"
Write-Host ""
Write-Host "To publish:"
Write-Host "  1. git commit + git push"
Write-Host "  2. GitHub > Releases > Draft a new release > tag v$Version"
Write-Host "  3. Attach Kairn-Setup-$Version.exe, then Publish release"
Write-Host "Installed copies of Kairn will offer the update on their own (requires a public repository)."
