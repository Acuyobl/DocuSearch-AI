# Ejecuta las suites deterministas del proyecto. Uso:
#   .\tests\run-all.ps1
#   .\tests\run-all.ps1 -IncludeDatabase   (requiere DOCUSEARCH_TEST_CONNECTION y SQL Server)
[CmdletBinding()]
param(
    [switch]$IncludeDatabase
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'DocuSearch.Checks.csproj'
$suites = @(
    [pscustomobject]@{ Name = 'Base'; Args = @() },
    [pscustomobject]@{ Name = 'AG-02 Verificador'; Args = @('--verifier') },
    [pscustomobject]@{ Name = 'AG-10 Inspector'; Args = @('--inspector') },
    [pscustomobject]@{ Name = 'AG-01 Catalogador'; Args = @('--catalog') },
    [pscustomobject]@{ Name = 'AG-05 Auditor'; Args = @('--audit') },
    [pscustomobject]@{ Name = 'Bloqueo de login'; Args = @('--auth') },
    [pscustomobject]@{ Name = 'RAG híbrido'; Args = @('--rag') }
)
$failed = [System.Collections.Generic.List[string]]::new()

foreach ($suite in $suites) {
    Write-Host "`n=== $($suite.Name) ===" -ForegroundColor Cyan
    $arguments = @('run', '--project', $project, '-c', 'Debug')
    if ($suite.Args.Count -gt 0) {
        $arguments += '--'
        $arguments += $suite.Args
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { $failed.Add($suite.Name) }
}

if ($IncludeDatabase) {
    if ([string]::IsNullOrWhiteSpace($env:DOCUSEARCH_TEST_CONNECTION)) {
        Write-Warning 'DOCUSEARCH_TEST_CONNECTION no está definido; se omite la integración con SQL Server.'
    }
    else {
        Write-Host "`n=== Integración SQL Server (--database) ===" -ForegroundColor Cyan
        & dotnet run --project $project -c Debug -- --database
        if ($LASTEXITCODE -ne 0) { $failed.Add('SQL Server (--database)') }
    }
}

if ($failed.Count -gt 0) {
    Write-Error ("Suites con fallos: " + ($failed -join ', '))
    exit 1
}

Write-Host "`nTodas las suites ejecutadas pasaron." -ForegroundColor Green
