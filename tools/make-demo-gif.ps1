# Renders the README demo (docs/demo.gif) off-screen: no screen capture involved.
# Needs ffmpeg on the PATH (winget install Gyan.FFmpeg).
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-demo-gif.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

dotnet build Mimi.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$frames = Join-Path $env:TEMP "mimi-demo-frames"
if (Test-Path $frames) { Remove-Item -Recurse -Force $frames }
& .\bin\Release\net48\Mimi.exe --record-demo $frames | Out-Null
while (Get-Process Mimi -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*bin\Release*" }) { Start-Sleep -Milliseconds 200 }

New-Item -ItemType Directory -Force docs | Out-Null
$filters = "fps=20,scale=720:-1:flags=lanczos"
ffmpeg -loglevel error -y -framerate 30 -i "$frames\frame_%04d.png" -vf "$filters,palettegen=stats_mode=diff:max_colors=200" "$frames\palette.png"
ffmpeg -loglevel error -y -framerate 30 -i "$frames\frame_%04d.png" -i "$frames\palette.png" -lavfi "$filters [x]; [x][1:v] paletteuse=dither=sierra2_4a:diff_mode=rectangle" -loop 0 docs\demo.gif
Remove-Item -Recurse -Force $frames
"docs\demo.gif: {0:N0} KB" -f ((Get-Item docs\demo.gif).Length / 1KB)
