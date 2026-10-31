param([switch]$SkipRuntime)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$env:Path=[Environment]::GetEnvironmentVariable('Path','Machine')+';'+[Environment]::GetEnvironmentVariable('Path','User')
if(-not $SkipRuntime){
    $taskArchive=Join-Path $env:TEMP 'evoflow-python-3.12.10-embed-amd64.zip'
    if(-not(Test-Path -LiteralPath $taskArchive)){
        Invoke-WebRequest 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $taskArchive
    }
    New-Item -ItemType Directory -Force runtime | Out-Null
    Expand-Archive -LiteralPath $taskArchive -DestinationPath runtime -Force
    @('python312.zip','.','Lib\site-packages','..\worker','import site') | Set-Content -LiteralPath runtime\python312._pth -Encoding ascii
    uv pip install --link-mode=copy --python .venv\Scripts\python.exe --target runtime\Lib\site-packages --require-hashes -r requirements.lock
    if($LASTEXITCODE -ne 0){throw 'Runtime dependency installation failed'}
    & runtime\python.exe -I -c 'import fastapi,langgraph,sqlite3,docx,pptx,openpyxl,reportlab,pdfplumber,pypdf; from evoflow_worker.api import create_app; print("Bundled runtime imports passed")'
    if($LASTEXITCODE -ne 0){throw 'Bundled runtime verification failed'}
    [ordered]@{python_version='3.12.10';archive_sha256=(Get-FileHash -LiteralPath $taskArchive).Hash;python_signature=(Get-AuthenticodeSignature runtime\python.exe).Status.ToString()} | ConvertTo-Json | Set-Content docs\evidence\python-runtime.json
}
& (Join-Path $PSScriptRoot 'Sync-CuaRuntime.ps1')
winapp package native\EvoFlow\EvoFlow.csproj --arch x64 --no-sign --self-contained -p Platform=x64 -p CreatePackageMakeAppxParameters=/np
if($LASTEXITCODE -ne 0){throw 'Native package build failed'}
$taskPackage=Join-Path $taskRoot 'EvoFlow_1.0.0.0_x64.msix'
if(-not(Test-Path -LiteralPath $taskPackage -PathType Leaf)){throw 'Expected package not returned; inspect winapp output before preparing delivery.'}
$taskDelivery=Join-Path $taskRoot ('delivery\EvoFlow-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskDelivery | Out-Null
Copy-Item -LiteralPath $taskPackage -Destination $taskDelivery
$taskLayout=Join-Path $taskDelivery 'app'
[IO.Compression.ZipFile]::ExtractToDirectory($taskPackage,$taskLayout)
foreach($taskRequired in @('EvoFlow.exe','EvoFlow.Connectors.dll','tools/EvoFlow.ToolHost.exe','tools/EvoFlow.ToolHost.dll','runtime/python.exe','runtime/cua/cua-driver.exe','runtime/cua/cua-driver-uia.exe','runtime/cua/cua-cursor-theme.exe','runtime/cua/LICENSE.txt','config/cua-runtime.json','worker/evoflow_worker/local_tools.py')){
    if(-not(Test-Path -LiteralPath (Join-Path $taskLayout $taskRequired) -PathType Leaf)){throw "Incomplete package: missing $taskRequired"}
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Launch-Delivery.ps1') -Destination (Join-Path $taskDelivery 'Launch.ps1')
foreach($taskRequired in @('Assets/AppIcon.ico','Assets/AppIcon.png','Assets/TrayIcon.ico','Assets/TrayGlyph.png','worker/evoflow_worker/monitoring.py')){
    if(-not(Test-Path -LiteralPath (Join-Path $taskLayout $taskRequired) -PathType Leaf)){throw "Incomplete branded/monitored package: missing $taskRequired"}
}
@'
EvoFlow native x64 development delivery

Launch with PowerShell: .\Launch.ps1
This registers the included native app layout with WinApp CLI and launches it.
Requires Windows Developer Mode and WinApp CLI 0.7.1. Python, .NET and the
Windows App SDK are included. No source checkout or .venv is required.
Cua Driver 0.38.0 and its required Windows payload and MIT notice are included.
Calculator must be installed. The reviewed Cua desktop scope is Calculator;
the separate browser scope uses an isolated profile and selenium.dev. The
current Cua browser preparation ownership refusal remains unresolved; see
docs/MCP-CUA-VERIFICATION-2026-10-31.md in the source checkout for evidence.

The MSIX is unsigned; it is not a normal trusted installer. Production signing
and a clean-machine installer test remain separate work. Launch does not trust
a certificate. Settings and protected credentials live in LocalAppData\EvoFlow.
Supabase uses an automatic protected device session; OpenRouter key entry is
in the separate Settings window. Network is needed for acquisition and sync.
'@ | Set-Content -LiteralPath (Join-Path $taskDelivery 'README.txt') -Encoding utf8
[ordered]@{package=$taskPackage;sha256=(Get-FileHash -LiteralPath $taskPackage).Hash;layout=$taskLayout;signing='unsigned';time=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskDelivery 'delivery.json')
Write-Output "Delivery prepared at $taskDelivery"
