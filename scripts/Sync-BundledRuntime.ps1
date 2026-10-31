$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $taskRoot
try {
    if(-not(Test-Path -LiteralPath runtime/python.exe)){throw 'Run scripts/Build-Delivery.ps1 to acquire the embedded Python runtime first.'}
    uv export --frozen --no-dev --no-emit-project --format requirements-txt --output-file requirements.lock | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Locked dependency export failed'}
    uv pip install --link-mode=copy --python .venv/Scripts/python.exe --target runtime/Lib/site-packages --require-hashes -r requirements.lock
    if($LASTEXITCODE -ne 0){throw 'Bundled dependency installation failed'}
    & runtime/python.exe -I -c 'import docx,pptx,openpyxl,reportlab,pdfplumber,pypdf,pydantic; print("Bundled document runtime imports passed")'
    if($LASTEXITCODE -ne 0){throw 'Bundled document dependency verification failed'}
} finally { Pop-Location }
