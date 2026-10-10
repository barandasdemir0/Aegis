[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [switch]$RequireNoSkipped
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resultFiles = @(Get-ChildItem -LiteralPath $Path -Filter "*.trx" -File -Recurse)
if ($resultFiles.Count -eq 0) {
    throw "TRX test sonucu bulunamadı: $Path"
}

$total = 0
$passed = 0
$failed = 0
$skipped = 0
$incompleteRuns = 0
foreach ($resultFile in $resultFiles) {
    [xml]$document = Get-Content -LiteralPath $resultFile.FullName -Raw
    $summary = $document.TestRun.ResultSummary
    $counters = $summary.Counters
    if ($null -eq $counters) {
        throw "TRX sayaçları okunamadı: $($resultFile.FullName)"
    }

    if ([string]$summary.outcome -ne "Completed") {
        $incompleteRuns++
    }

    $total += [int]$counters.total
    $passed += [int]$counters.passed
    $failed += [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
    $skipped += [int]$counters.notExecuted + [int]$counters.notRunnable
}

Write-Host "TRX özeti: total=$total passed=$passed failed=$failed skipped=$skipped incomplete=$incompleteRuns files=$($resultFiles.Count)"
if ($total -eq 0 -or $incompleteRuns -gt 0) {
    throw "Test çalışması tamamlanmadı veya hiç test yürütülmedi."
}
if ($failed -gt 0) {
    throw "Test sonuçlarında $failed başarısız/yarıda kesilmiş test var."
}
if ($RequireNoSkipped -and $skipped -gt 0) {
    throw "Yayın kapısında atlanan test kabul edilmez: $skipped test atlandı."
}
