#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GamePath = '',
    [string]$VsImGuiPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $GamePath = Join-Path $workspacePath '../Vintagestory'
}
if ([string]::IsNullOrWhiteSpace($VsImGuiPath)) {
    $VsImGuiPath = Join-Path $workspacePath '../vsimgui_1.2.7'
}
$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
$VsImGuiPath = (Resolve-Path -LiteralPath $VsImGuiPath).Path
$projectPath = Join-Path $workspacePath 'tests/Seraph.DebugUI.Tests/Seraph.DebugUI.Tests.csproj'
& dotnet run --project $projectPath -c Release "-p:VsGamePath=$GamePath" "-p:VsImGuiPath=$VsImGuiPath" -- $workspacePath $VsImGuiPath
if ($LASTEXITCODE -ne 0) { throw "Native UI checks failed with exit code $LASTEXITCODE" }
