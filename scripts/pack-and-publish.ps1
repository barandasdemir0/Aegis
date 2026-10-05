param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [string]$SourceUrl = "",
    [string]$ApiKey = ""
)

if ($OutputDir -eq "") {
    $OutputDir = Join-Path $PSScriptRoot "..\artifacts"
}

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "  Aegis NuGet Paketleme ve Dagitim      " -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

if (Test-Path $OutputDir) {
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "[1/3] xUnit Testleri Calistiriliyor..." -ForegroundColor Yellow
$testProj = Join-Path $PSScriptRoot "..\tests\Aegis.Tests\Aegis.Tests.csproj"
dotnet test $testProj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "HATA: Testler basarisiz oldu!" -ForegroundColor Red
    exit 1
}
Write-Host "Testler basariyla gecti!" -ForegroundColor Green

Write-Host "[2/3] NuGet Paketleri Uretiliyor..." -ForegroundColor Yellow
# Yalnizca src altindaki kutuphane projeleri paketlenir; ornek ve test projeleri
# IsPackable=false oldugu icin cozum genelinde pack yapilsa dahi paket uretmez.
$srcDir = Join-Path $PSScriptRoot "..\src"
$projects = Get-ChildItem -Path $srcDir -Filter "*.csproj" -Recurse

foreach ($proj in $projects) {
    Write-Host "  Paketleniyor: $($proj.BaseName)" -ForegroundColor Gray
    dotnet pack $proj.FullName -c $Configuration -o $OutputDir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "HATA: $($proj.BaseName) paketlenemedi!" -ForegroundColor Red
        exit 1
    }
}

Write-Host "[3/3] Uretilen Paketler:" -ForegroundColor Yellow
$packages = Get-ChildItem -Path $OutputDir -Filter "*.nupkg"
foreach ($pkg in $packages) {
    $sizeKb = [math]::Round($pkg.Length / 1KB, 2)
    Write-Host "  $($pkg.Name) ($sizeKb KB)" -ForegroundColor Green
}

if ($SourceUrl -ne "") {
    Write-Host "Paketler hedefe yukleniyor: $SourceUrl..." -ForegroundColor Yellow
    foreach ($pkg in $packages) {
        if ($ApiKey -ne "") {
            dotnet nuget push $pkg.FullName --source $SourceUrl --api-key $ApiKey --skip-duplicate
        } else {
            dotnet nuget push $pkg.FullName --source $SourceUrl --skip-duplicate
        }
    }
    Write-Host "Yukleme islemi tamamlandi!" -ForegroundColor Green
} else {
    Write-Host "Paketler yerel klasorde hazirlandi: $OutputDir" -ForegroundColor Cyan
}
