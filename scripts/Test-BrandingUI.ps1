param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-brand-acceptance', [switch]$ReuseConversation)
# Explicit live synthetic conversation; uses saved configured provider keys.
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$taskPrevious=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-brand-acceptance'
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$taskArtifact=[IO.Path]::GetFullPath($ArtifactDirectory)
$taskReport=[ordered]@{appPid=$AppPid;target='local';status='FAIL';checks=@();time=(Get-Date).ToString('o')}
function UI([string[]]$Arguments){Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments $Arguments}
try {
    $taskWindows=@(& winapp ui list-windows --on local -a $AppPid --json) -join "`n" | ConvertFrom-Json
    foreach($taskWindow in $taskWindows | Where-Object title -eq 'EvoFlow · Memory'){
        & winapp ui send-keys 'alt+f4' --via send-input --allow-system-keys --on local -w $taskWindow.hwnd | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Could not close owned Memory window'}
    }
    $taskInitial=UI @('inspect','--json')
    if($taskInitial -notmatch 'TaskText'){UI @('invoke','Pill') | Out-Null}
    UI @('wait-for','TaskText','-t','5000') | Out-Null
    UI @('invoke','PinButton','--action','toggle-on') | Out-Null
    if(-not $ReuseConversation){
        UI @('invoke','NewChatButton') | Out-Null
        UI @('set-value','TaskText','Give a concise three-item checklist for reviewing a completed workflow: check the output, verify evidence, and save reusable steps. Keep it under 60 words. Do not use tools.') | Out-Null
        UI @('invoke','RunButton') | Out-Null
    }
    UI @('wait-for','RunButton','-p','IsEnabled','--value','True','-t','180000') | Out-Null
    $taskTree=UI @('inspect','--json','-d','14')
    if($taskTree -notmatch '(?s)"name"\s*:\s*"1\.[^"]*(?:[Vv]erify|[Ee]vidence)'){throw 'Numbered synthetic assistant response was not observed'}
    UI @('hover','TaskText','--dwell-time','700') | Out-Null
    UI @('screenshot','--capture-screen','--output',(Join-Path $taskArtifact 'conversation.png'),'--json') | Out-Null
    $taskReport.checks+=if($ReuseConversation){'Persisted synthetic model conversation restored in the native UI'}else{'Actual native model conversation completed with new packaged icon'}
    $taskProcess=Get-Process -Id $AppPid
    Add-Type -AssemblyName System.Drawing
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class EvoFlowBrandIdentity {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder value);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
'@
    $taskMain=[IntPtr][long](Get-EvoFlowWindow -AppPid $AppPid -Title 'EvoFlow')
    $taskIconHandle=[EvoFlowBrandIdentity]::SendMessage($taskMain,0x7f,[IntPtr]1,[IntPtr]0)
    if($taskIconHandle -eq 0){throw 'Native window has no explicit large icon'}
    $taskIcon=[Drawing.Icon]::FromHandle($taskIconHandle)
    $taskBitmap=$taskIcon.ToBitmap(); $taskBitmap.Save((Join-Path $taskArtifact 'native-window-icon.png')); $taskBitmap.Dispose()
    $taskExeIcon=[Drawing.Icon]::ExtractAssociatedIcon($taskProcess.Path)
    $taskBitmap=$taskExeIcon.ToBitmap(); $taskBitmap.Save((Join-Path $taskArtifact 'compiled-executable-icon.png')); $taskBitmap.Dispose(); $taskExeIcon.Dispose()
    [uint32]$taskLength=512; $taskAumid=[Text.StringBuilder]::new(512)
    $taskCode=[EvoFlowBrandIdentity]::GetApplicationUserModelId($taskProcess.Handle,[ref]$taskLength,$taskAumid)
    if($taskCode -ne 0 -or $taskAumid.ToString() -notmatch '^12A42CE7-BC8B-448C-9C72-2DEB315E6F97_.*!App$'){throw 'Native package taskbar identity was not preserved'}
    $taskReport.aumid=$taskAumid.ToString(); $taskReport.executable=$taskProcess.Path
    $taskReport.checks+='Actual native window icon and compiled executable icon extracted; existing package AUMID verified'
    UI @('invoke','PinButton','--action','toggle-off') | Out-Null
    UI @('send-keys','esc','--via','send-input') | Out-Null
    UI @('wait-for','TaskText','--gone','-t','5000') | Out-Null
    UI @('screenshot','--capture-screen','--output',(Join-Path $taskArtifact 'compact.png'),'--json') | Out-Null
    $taskReport.status='PASS'
} catch {$taskReport.error=$_.Exception.Message}
finally {
    & winapp ui yield --on local | Out-Null
    $env:WINAPP_UI_WORKFLOW_ID=$taskPrevious
    $taskReport | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskArtifact 'results.json')
}
$taskReport | ConvertTo-Json -Depth 5
if($taskReport.status -ne 'PASS'){exit 1}
