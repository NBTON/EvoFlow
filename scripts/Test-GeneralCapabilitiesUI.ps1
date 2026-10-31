param([Parameter(Mandatory)][int]$AppPid,[string]$ArtifactDirectory='docs/evidence/ui-general-capabilities')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-general-capabilities'
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$checks=[Collections.Generic.List[string]]::new()
$filename='EvoFlow-general-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.pdf'
$document=Join-Path (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'EvoFlow Files') $filename
function UI($a){Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments $a}
function Nodes($ns){foreach($n in $ns){$n;Nodes $n.children}}
function Tree { $t=UI @('inspect','-d','18','--json') | ConvertFrom-Json; @(Nodes $t.windows.elements) }
function WaitReady($approvalPattern='^$') {
    for($attempt=0;$attempt -lt 300;$attempt++){
        $tree=Tree
        $approval=@($tree | Where-Object {$_.automationId -eq 'ApproveToolButton' -and $_.isEnabled})
        if($approval.Count -gt 0){
            $description=@($tree | Where-Object {$_.automationId -eq 'ToolApprovalDescription'})
            if($description.Count -ne 1 -or $description[0].name -notmatch $approvalPattern){throw 'Unexpected approval outside the authorized test action'}
            if($approval[-1].isOffscreen){throw 'Approval is offscreen'}
            $area=@($tree | Where-Object automationId -eq 'ToolApprovals')
            if($area.Count -eq 1 -and ($approval[-1].y -lt $area[0].y -or ($approval[-1].y+$approval[-1].height) -gt ($area[0].y+$area[0].height+1))){throw 'Approval button is clipped by the action viewport'}
            UI @('screenshot','--output',(Join-Path $ArtifactDirectory ('approval-'+$attempt+'.png')),'--json') | Out-Null
            UI @('invoke',$approval[-1].selector) | Out-Null
        }
        if($tree | Where-Object {$_.automationId -eq 'RunButton' -and $_.isEnabled}){return}
        Start-Sleep -Milliseconds 500
    }
    throw 'Agent deadline exceeded'
}
function Send($text,$approvalPattern='^$'){
    UI @('set-value','TaskText',$text) | Out-Null; UI @('invoke','RunButton') | Out-Null
    Start-Sleep -Milliseconds 300; WaitReady $approvalPattern
}
function Screenshot($name){UI @('screenshot','--output',(Join-Path $ArtifactDirectory $name),'--json') | Out-Null}
$report=[ordered]@{status='FAIL';appPid=$AppPid;target='local';document=$document;checks=@()}
try {
    if(-not(Tree | Where-Object automationId -eq 'TaskText')){UI @('invoke','Pill') | Out-Null}
    WaitReady
    UI @('invoke','NewChatButton') | Out-Null; WaitReady
    Send 'Open Word.'
    if(-not(Tree | Where-Object name -match 'Word.*window is available')){throw 'Word launch did not return verified tool evidence'}
    if(-not(Get-Process WINWORD -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0)){throw 'Independent Word window check failed'}
    Screenshot 'word-open.png'; $checks.Add('Actual agent opened Microsoft Word through installed-app discovery; independent WINWORD window check passed')
    Send 'Run ipconfig with no arguments through terminal_run. Report only whether it executed and its exit code; omit network details.' '(?s)^Run [^\r\n]+\\ipconfig\.exe\r?\nArguments: \[\]'
    if(-not(Tree | Where-Object name -match 'Process exited with code 0')){throw 'Approved general command did not execute successfully'}
    Screenshot 'terminal-approved.png'; $checks.Add('Non-allowlisted native executable ipconfig ran after visible exact-command approval and returned exit code 0')
    Send 'Use the browser tool to navigate to https://example.com and report its page title. Use the browser tool, not application_launch.' '(?s)^Browser navigate\r?\nurl: https://example\.com/?\r?\n|^Browser inspect'
    if(-not(Tree | Where-Object name -like '*Example Domain*')){throw 'Browser did not report the expected page title'}
    Screenshot 'browser-verified.png'; $checks.Add('Actual agent used packaged Playwright browser and returned Example Domain from live page inspection')
    Send "Create a PDF at '$document' with title General Tools and paragraph 'Original content'. Then edit that PDF: replace 'Original' with 'Revised', stamp 'APPROVED' at x=36, y=200, and rotate page 0 clockwise by 90 degrees. Verify the saved changes." ([regex]::Escape($document))
    if(-not(Test-Path -LiteralPath $document)){throw 'PDF was not created'}
    Screenshot 'pdf-edited.png'; $checks.Add('Actual agent created and edited a PDF with text replacement, stamp and rotation after native approval')
    $report.sha256=(Get-FileHash -LiteralPath $document).Hash
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally {try{& winapp ui yield --on local | Out-Null}finally{$env:WINAPP_UI_WORKFLOW_ID=$previous}}
$report.checks=@($checks.ToArray())
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $ArtifactDirectory 'test-results.json')
if($report.status -ne 'PASS'){exit 1}
