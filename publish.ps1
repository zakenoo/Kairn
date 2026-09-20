# Prépare une version de Kairn à publier sur GitHub.
#   .\publish.ps1            → reconstruit la version actuelle
#   .\publish.ps1 1.0.1      → passe en 1.0.1 puis construit
# Résultat : release\Kairn-Setup-<version>.exe (le setup, qui sert aussi aux mises à jour automatiques).

param([string]$Version)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "Kairn\Kairn.csproj"

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version attendue au format 1.2.3" }
    # Lecture et écriture en UTF-8 sans BOM, explicitement : sinon PowerShell 5.1 relit le fichier
    # comme de l'ANSI et réécrit les accents en double encodage (« précompilé » → « prÃ©compilÃ© »).
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $text = [System.IO.File]::ReadAllText($csproj, $utf8) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    [System.IO.File]::WriteAllText($csproj, $text, $utf8)
}
$Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$out = Join-Path $root "release"
$build = Join-Path $out "build"
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $build --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

$setup = Join-Path $out "Kairn-Setup-$Version.exe"
Copy-Item (Join-Path $build "Kairn.exe") $setup -Force
Remove-Item $build -Recurse -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host "Prêt : $setup" -ForegroundColor Green
Write-Host "SHA-256 : $hash"
Write-Host ""
Write-Host "Pour publier :"
Write-Host "  1. git commit + git push"
Write-Host "  2. GitHub > Releases > Draft a new release > tag v$Version"
Write-Host "  3. Joindre Kairn-Setup-$Version.exe, puis Publish release"
Write-Host "Les Kairn installés proposeront la mise à jour d'eux-mêmes (dépôt public requis)."
