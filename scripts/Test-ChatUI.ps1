param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-chat')
# Keep the established entry point while testing actual conversations. This sends
# real model requests using saved provider keys; fictional demos are separate.
& (Join-Path $PSScriptRoot 'Test-ConversationSessions.ps1') -AppPid $AppPid -ArtifactDirectory $ArtifactDirectory
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
