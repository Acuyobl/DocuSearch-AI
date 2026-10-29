$ErrorActionPreference = 'Stop'
$environmentPath = Join-Path $env:LOCALAPPDATA 'DocuSearch\LangChain\.venv'
if (-not (Test-Path -LiteralPath (Join-Path $environmentPath 'Scripts\python.exe'))) {
    py -3.14 -m venv $environmentPath
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear el entorno Python 3.14.' }
}
& (Join-Path $environmentPath 'Scripts\python.exe') -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'No se pudieron instalar las dependencias de LangChain.' }
& (Join-Path $environmentPath 'Scripts\python.exe') -I (Join-Path $PSScriptRoot 'worker.py') --check
if ($LASTEXITCODE -ne 0) { throw 'LangChain no supero la comprobacion de arranque.' }
