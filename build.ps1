# Compile Mimi et dépose l'exécutable final dans dist\Mimi.exe
# Usage :  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet build Mimi.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué" }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item bin\Release\net48\Mimi.exe dist\Mimi.exe -Force

$size = [Math]::Round((Get-Item dist\Mimi.exe).Length / 1KB)
Write-Host "OK -> dist\Mimi.exe ($size Ko)" -ForegroundColor Magenta
