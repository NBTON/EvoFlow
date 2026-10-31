param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-top-anchored')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-top-anchor'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$artifact=[IO.Path]::GetFullPath($ArtifactDirectory)
$report=[ordered]@{target='local';appPid=$AppPid;time=(Get-Date).ToString('o');status='FAIL';checks=@();screenshots=@()}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
function Rect {
    $main=Get-EvoFlowWindow -AppPid $AppPid -Title 'EvoFlow'
    $tree=Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments @('inspect','--json') | ConvertFrom-Json
    $element=$tree.windows[0].elements[0]
    [pscustomobject]@{x=$element.x; y=$element.y; w=$element.width; h=$element.height; hwnd=$main}
}
function Assert-Top([pscustomobject]$rect, [string]$phase) {
    if($rect.y -ne 0){throw "$phase window y=$($rect.y); expected flush with the top edge (y=0)"}
    if($rect.h -le 0 -or $rect.w -le 0){throw "$phase has no visible size"}
    $report.checks += "$phase anchored at top edge: y=0, $($rect.w)x$($rect.h)"
}
function Save-DesktopStrip([string]$name) {
    # Physical top-center strip with desktop/taskbar context so the flush seam is visible.
    $bounds=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $stripWidth=[Math]::Min(900, $bounds.Width); $stripHeight=[Math]::Min(300, $bounds.Height)
    $bitmap=New-Object System.Drawing.Bitmap($stripWidth, $stripHeight)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int](($bounds.Width-$stripWidth)/2), 0, 0, 0, (New-Object System.Drawing.Size($stripWidth, $stripHeight)))
    $graphics.Dispose()
    $path=Join-Path $artifact $name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Strip not saved: $path"}
    $report.screenshots += $path
}
try {
    $compact=Rect
    Assert-Top $compact 'Compact pill'
    if($compact.w -ne 280 -or $compact.h -ne 44){throw "Compact pill expected 280x44 at 100% scale; got $($compact.w)x$($compact.h)"}
    Save-DesktopStrip 'desktop-compact-top.png'
    Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments @('invoke','Pill') | Out-Null
    Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments @('wait-for','TaskText','-t','5000') | Out-Null
    Start-Sleep -Milliseconds 400  # let the 180ms expand animation settle
    $expanded=Rect
    Assert-Top $expanded 'Expanded panel'
    if($expanded.h -le $compact.h){throw 'Panel did not expand downward from the fixed top edge'}
    Save-DesktopStrip 'desktop-expanded-top.png'
    Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments @('send-keys','esc','--via','send-input') | Out-Null
    Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments @('wait-for','TaskText','--gone','-t','5000') | Out-Null
    Start-Sleep -Milliseconds 400
    $restored=Rect
    Assert-Top $restored 'Recollapsed pill'
    if($restored.w -ne $compact.w -or $restored.h -ne $compact.h){throw "Collapse did not restore compact size: $($restored.w)x$($restored.h)"}
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally {
    try { & winapp ui yield --on local | Out-Null; if($LASTEXITCODE -ne 0){throw 'UI yield failed'} }
    catch { $report.status='FAIL'; $report.cleanupError=$_.Exception.Message }
    $env:WINAPP_UI_WORKFLOW_ID=$previous
}
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $artifact 'top-anchor-results.json')
if($report.status -ne 'PASS'){exit 1}
