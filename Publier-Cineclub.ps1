Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetCommand = Get-Command -Name 'dotnet' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $dotnetCommand) {
    throw 'Le SDK .NET 10 est requis. Installe-le depuis https://dotnet.microsoft.com/download/dotnet/10.0 puis relance ce script.'
}

$dotnet = $dotnetCommand.Source
$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $sdkVersionOutput = & $dotnet --version 2>$null
    $sdkVersionExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($sdkVersionExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersionOutput)) {
    throw "Le SDK .NET 10 est requis, mais aucun SDK compatible n'a été trouvé par la commande dotnet."
}

$sdkVersion = ([string]$sdkVersionOutput).Trim()
if ($sdkVersion -notmatch '^10\.') {
    throw "Le SDK .NET 10 est requis; la version sélectionnée est '$sdkVersion'."
}

Push-Location $projectRoot
try {
    & (Join-Path $projectRoot 'Obtenir-Cloudflared.ps1')

    & $dotnet publish '.\Cineclub.App\Cineclub.App.csproj' `
        -warnaserror `
        -p:PublishProfile='.\Properties\PublishProfiles\WinX64.pubxml'
    if ($LASTEXITCODE -ne 0) {
        throw "La publication .NET a échoué avec le code $LASTEXITCODE."
    }

    $publishDirectory = Join-Path $projectRoot 'publish\win-x64'
    foreach ($fileName in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $fileName) -Destination $publishDirectory -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publishDirectory -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishDirectory -Recurse -Force

    Write-Host ''
    Write-Host 'Cineclub 2.0 est prêt dans :' -ForegroundColor Green
    Write-Host $publishDirectory -ForegroundColor Cyan
}
finally {
    Pop-Location
}
