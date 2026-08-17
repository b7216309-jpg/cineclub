[CmdletBinding()]
param(
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '2026.7.3'
$expectedSha256 = '8635DA433B6DF8194746E88ED9D2589566C20E38BFC2A80E431A348B7C765841'
$downloadUri = "https://github.com/cloudflare/cloudflared/releases/download/$version/cloudflared-windows-amd64.exe"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsDirectory = Join-Path $projectRoot 'Cineclub.App\Tools'
$destination = Join-Path $toolsDirectory 'cloudflared.exe'

if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    $currentHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($currentHash -eq $expectedSha256) {
        Write-Host "cloudflared $version est déjà présent et vérifié." -ForegroundColor Green
        return
    }

    Write-Warning 'Le binaire cloudflared existant ne correspond pas à la version épinglée; il sera remplacé.'
}

New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$temporaryPath = Join-Path ([IO.Path]::GetTempPath()) "cineclub-cloudflared-$PID-$([Guid]::NewGuid().ToString('N')).exe"

try {
    Write-Host "Téléchargement de cloudflared $version…" -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $downloadUri -OutFile $temporaryPath

    $downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash
    if ($downloadedHash -ne $expectedSha256) {
        throw "SHA-256 invalide pour cloudflared. Attendu: $expectedSha256; reçu: $downloadedHash."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $destination -Force
    Write-Host "cloudflared $version installé et vérifié dans $destination" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
