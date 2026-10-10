[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [string]$SourceUrl = "",
    [string]$ApiKey = "",
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repositoryRoot "artifacts\packages"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
    if ($SkipTests) {
        throw "Yayın sırasında -SkipTests kullanılamaz."
    }

    $requiredIntegrationVariables = @("AEGIS_TEST_REDIS", "AEGIS_TEST_TOXIPROXY", "AEGIS_TEST_SQL")
    $missingIntegrationVariables = @($requiredIntegrationVariables | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    })
    if ($missingIntegrationVariables.Count -gt 0) {
        throw "Yayın için gerçek servis test değişkenleri zorunludur: $($missingIntegrationVariables -join ', ')."
    }

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        throw "Yayın için -ApiKey zorunludur. Anahtarı kaynak koda veya betiğe yazmayın."
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Description,
        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host $Description -ForegroundColor Yellow
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description başarısız oldu (çıkış kodu: $LASTEXITCODE)."
    }
}

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "  Aegis NuGet paketleme ve dağıtım" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
Get-ChildItem -LiteralPath $OutputDir -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in ".nupkg", ".snupkg" } |
    Remove-Item -Force

if (-not $SkipTests) {
    $testResultsDir = Join-Path $OutputDir "test-results"
    New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
    Get-ChildItem -LiteralPath $testResultsDir -Filter "*.trx" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Invoke-CheckedCommand "[1/5] Ana testler çalıştırılıyor..." {
        dotnet test (Join-Path $repositoryRoot "tests\Aegis.Tests\Aegis.Tests.csproj") -c $Configuration --nologo --disable-build-servers -m:1 -p:BuildInParallel=false --results-directory $testResultsDir --logger "trx;LogFilePrefix=Aegis.Tests"
    }
    Invoke-CheckedCommand "[2/5] .NET Framework uyumluluk testleri çalıştırılıyor..." {
        dotnet test (Join-Path $repositoryRoot "tests\Aegis.CompatibilityTests\Aegis.CompatibilityTests.csproj") -c $Configuration --nologo --disable-build-servers -m:1 -p:BuildInParallel=false --results-directory $testResultsDir --logger "trx;LogFilePrefix=Aegis.CompatibilityTests"
    }
    Invoke-CheckedCommand "[3/5] Batırma testleri çalıştırılıyor..." {
        dotnet run --project (Join-Path $repositoryRoot "tests\Aegis.TortureTests\Aegis.TortureTests.csproj") -c $Configuration -- 1 (Join-Path $OutputDir "torture-report.md")
    }
    if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
        & (Join-Path $PSScriptRoot "assert-test-results.ps1") -Path $testResultsDir -RequireNoSkipped
    }
} else {
    Write-Host "[1-3/5] Testler açıkça atlandı." -ForegroundColor DarkYellow
}

Write-Host "[4/5] NuGet paketleri üretiliyor..." -ForegroundColor Yellow
$projects = @(Get-ChildItem -Path (Join-Path $repositoryRoot "src") -Filter "*.csproj" -Recurse)
foreach ($project in $projects) {
    Invoke-CheckedCommand "  Paketleniyor: $($project.BaseName)" {
        dotnet pack $project.FullName -c $Configuration -o $OutputDir --nologo --disable-build-servers -m:1 -p:BuildInParallel=false
    }
}

$packages = @(Get-ChildItem -LiteralPath $OutputDir -Filter "*.nupkg" -File |
    Where-Object { $_.Extension -eq ".nupkg" })
$symbolPackages = @(Get-ChildItem -LiteralPath $OutputDir -Filter "*.snupkg" -File)
if ($packages.Count -ne $projects.Count -or $symbolPackages.Count -ne $projects.Count) {
    throw "Paket sayısı uyuşmuyor: $($projects.Count) proje, $($packages.Count) nupkg, $($symbolPackages.Count) snupkg."
}

Write-Host "[5/5] Üretilen paketler doğrulanıyor..." -ForegroundColor Yellow
foreach ($package in $packages) {
    if ($package.Length -eq 0) {
        throw "Boş paket üretildi: $($package.FullName)"
    }
    Write-Host "  $($package.Name) ($([math]::Round($package.Length / 1KB, 2)) KB)" -ForegroundColor Green
}

if (-not [string]::IsNullOrWhiteSpace($SourceUrl)) {
    Write-Host "Paketler hedefe yükleniyor: $SourceUrl" -ForegroundColor Yellow
    foreach ($package in $packages) {
        Invoke-CheckedCommand "  Yükleniyor: $($package.Name)" {
            dotnet nuget push $package.FullName --source $SourceUrl --api-key $ApiKey --skip-duplicate
        }
    }
    Write-Host "Tüm paketler başarıyla yüklendi." -ForegroundColor Green
} else {
    Write-Host "Paketler yerel klasörde hazırlandı: $OutputDir" -ForegroundColor Cyan
}
