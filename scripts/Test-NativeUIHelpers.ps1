$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
# Regression: results closed, main remains; stale results HWND must never be reused.
$windows=@(
    [pscustomobject]@{processId=42;title='EvoFlow';hwnd=100},
    [pscustomobject]@{processId=99;title='EvoFlow';hwnd=900}
)
$main=Select-EvoFlowWindow -Windows $windows -AppPid 42 -Title 'EvoFlow'
if($main -ne '100'){throw 'Selected wrong process/window'}
$rejected=$false
try { Select-EvoFlowWindow -Windows $windows -AppPid 42 -Title 'EvoFlow · Results & evidence' | Out-Null }
catch { $rejected=$true }
if(-not $rejected){throw 'Closed Results window was not rejected'}
$windows += [pscustomobject]@{processId=42;title='EvoFlow · Results & evidence';hwnd=200}
if((Select-EvoFlowWindow -Windows $windows -AppPid 42 -Title 'EvoFlow') -ne '100'){throw 'Results window replaced main target'}
'PASS: main target remains exact; missing Results rejected; unrelated PID excluded'
