$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$env:Path=[Environment]::GetEnvironmentVariable('Path','Machine')+';'+[Environment]::GetEnvironmentVariable('Path','User')
if(-not(Get-Command dotnet -ErrorAction SilentlyContinue)){throw 'Install .NET SDK 10.0.403 or a compatible 10.0 patch first.'}
if(-not(Get-Command winapp -ErrorAction SilentlyContinue)){throw 'Install Microsoft.WinAppCli 0.7.1 first.'}
if(-not(Get-Command uv -ErrorAction SilentlyContinue)){throw 'Install uv first: https://docs.astral.sh/uv/getting-started/installation/'}
uv sync --frozen --python 3.12 --link-mode=copy
if($LASTEXITCODE -ne 0){throw 'Python environment setup failed'}
@{python=(Join-Path $taskRoot '.venv\Scripts\python.exe');project=$taskRoot}|ConvertTo-Json|Set-Content native\EvoFlow\worker-location.json
dotnet restore native\EvoFlow\EvoFlow.csproj --locked-mode
if($LASTEXITCODE -ne 0){throw 'Native dependency restore failed'}
Write-Output 'Setup complete. Launch with: winapp run native\EvoFlow --detach --json'
