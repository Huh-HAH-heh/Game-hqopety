$ErrorActionPreference = "Stop"

$workspaceRoot = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $workspaceRoot "..\..\..")).Path
$exportedTree = Join-Path $workspaceRoot "exported\rimclone_unit_decision.xml"
$gameTree = Join-Path $repoRoot "Core\AI\Behaviac\BehaviacData\rimclone_unit_decision.xml"

if (-not (Test-Path -LiteralPath $exportedTree -PathType Leaf)) {
    throw "Behaviac Designer export not found: $exportedTree. Open rimclone_ai_cs.workspace.xml and export the tree as XML first."
}

[xml]$tree = Get-Content -LiteralPath $exportedTree -Raw
if ($tree.DocumentElement.Name -ne "behavior" -or
    $tree.DocumentElement.GetAttribute("name") -ne "rimclone_unit_decision") {
    throw "Expected a runtime-exported XML tree named 'rimclone_unit_decision'. Save the Designer source in behaviors; export the runtime XML into exported."
}

Copy-Item -LiteralPath $exportedTree -Destination $gameTree -Force
Write-Host "Installed Behaviac runtime tree:"
Write-Host "  $gameTree"
Write-Host "Build/run the game to use the updated policy."
