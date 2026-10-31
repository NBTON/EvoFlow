param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-local-tools')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-local-tools'
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$filename="EvoFlow-local-tools-$stamp.docx"
$workspace=Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'EvoFlow Files'
$document=Join-Path $workspace $filename
$checks=[Collections.Generic.List[string]]::new()
function UI($a){Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments $a}
function Nodes($ns){foreach($n in $ns){$n;Nodes $n.children}}
function Tree { $t=UI @('inspect','-d','18','--json') | ConvertFrom-Json; @(Nodes $t.windows.elements) }
function Ready([bool]$ApproveDocument=$false) {
    for($attempt=0;$attempt -lt 240;$attempt++){
        $tree=Tree
        $approval=@($tree | Where-Object {$_.automationId -eq 'ApproveToolButton' -and $_.isEnabled})
        if($approval.Count -gt 0){
            $description=@($tree | Where-Object {$_.automationId -eq 'ToolApprovalDescription' -and $_.name -like "*$filename*"})
            if(-not $ApproveDocument -or $description.Count -lt 1){throw 'Unexpected approval: only this test document is authorized'}
            if($approval[-1].isOffscreen){throw 'Native approval control is offscreen'}
            UI @('screenshot','--output',(Join-Path $ArtifactDirectory 'document-approval.png'),'--json') | Out-Null
            UI @('invoke',$approval[-1].selector) | Out-Null
        }
        $state=@($tree | Where-Object automationId -eq 'RunButton')
        if($state.Count -eq 1 -and $state[0].isEnabled){return}
        Start-Sleep -Milliseconds 500
    }
    throw 'Agent did not finish within UI deadline'
}
function Send($text,[bool]$ApproveDocument=$false){UI @('set-value','TaskText',$text) | Out-Null; UI @('invoke','RunButton') | Out-Null; Start-Sleep -Milliseconds 300; Ready $ApproveDocument}
$report=[ordered]@{target='local';appPid=$AppPid;status='FAIL';document=$document;checks=@()}
try {
    if(-not (Tree | Where-Object automationId -eq 'TaskText')){UI @('invoke','Pill') | Out-Null}
    Ready
    UI @('invoke','NewChatButton') | Out-Null
    Ready
    Send 'Open Chrome'
    $tree=Tree
    if(-not($tree | Where-Object name -like '*chrome window is available*')){throw 'Verified Chrome tool result missing from actual native conversation'}
    if(-not(Get-Process chrome -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0)){throw 'Independent Chrome window check failed'}
    UI @('screenshot','--output',(Join-Path $ArtifactDirectory 'chrome-verified.png'),'--json') | Out-Null
    $checks.Add('Open Chrome through actual chat; native application_launch returned verified window evidence; independent Chrome window check')
    Send "Create a Word document at '$document'. Title: EvoFlow Local Tools. Include this exact paragraph: Original document content. Use the document tool to create the actual file and verify the content."
    if(-not(Test-Path -LiteralPath $document)){throw 'Document file was not created'}
    $before=(Get-FileHash -LiteralPath $document).Hash
    UI @('screenshot','--output',(Join-Path $ArtifactDirectory 'document-created.png'),'--json') | Out-Null
    $checks.Add('Actual chat created a Word file through the native document tool and bundled runtime')
    Send "Edit the Word document at '$document': replace 'Original document content.' with 'Revised document content.' using the document tool. Then inspect the file to verify that the edit is present." $true
    $after=(Get-FileHash -LiteralPath $document).Hash
    if($before -eq $after){throw 'Document hash did not change after editing'}
    $archive=[IO.Compression.ZipFile]::OpenRead($document)
    try { $reader=[IO.StreamReader]::new($archive.GetEntry('word/document.xml').Open()); try{$xml=$reader.ReadToEnd()}finally{$reader.Dispose()} } finally{$archive.Dispose()}
    if($xml -notlike '*Revised document content.*' -or $xml -like '*Original document content.*'){throw 'Independent Word XML readback did not confirm the edit'}
    UI @('screenshot','--output',(Join-Path $ArtifactDirectory 'document-edited.png'),'--json') | Out-Null
    $report.beforeHash=$before; $report.afterHash=$after
    $checks.Add('Native approval matched the exact test file; actual chat edited and inspected it; independent XML readback and hash confirmed change')
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally { try{ & winapp ui yield --on local | Out-Null }finally{$env:WINAPP_UI_WORKFLOW_ID=$previous} }
$report.checks=@($checks.ToArray())
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $ArtifactDirectory 'test-results.json')
if($report.status -ne 'PASS'){exit 1}
