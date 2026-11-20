#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pages = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'assets/vsslssmod/config/handbook') -Filter '*.json' | Sort-Object Name | ForEach-Object {
    Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
})
if ($pages.Count -lt 18) { throw 'Missing handbook chapters.' }
$codes = @($pages.pageCode)
if (@($codes | Sort-Object -Unique).Count -ne $pages.Count) { throw 'Duplicate handbook page code.' }
foreach ($locale in @('en', 'zh-cn')) {
    $lang = Get-Content -LiteralPath (Join-Path $workspace "assets/vsslssmod/lang/$locale.json") -Raw | ConvertFrom-Json -AsHashtable
    $game = Get-Content -LiteralPath (Join-Path $workspace "assets/game/lang/$locale.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($page in $pages) {
        if ($page.categoryCode -ne 'vsslssmod' -or -not $game.ContainsKey('handbook-category-' + $page.categoryCode)) { throw 'Missing native handbook category.' }
        foreach ($field in @('title', 'text')) {
            $reference = $page.$field
            if ($reference -notmatch '^vsslssmod:(.+)$' -or -not $lang.ContainsKey($Matches[1])) { throw "Missing $locale handbook $field : $reference" }
        }
        $body = $lang[$page.text.Substring('vsslssmod:'.Length)]
        if ($body.Length -lt 700) { throw "Incomplete tutorial: $locale / $($page.pageCode)" }
        # The authored subset is XML-compatible and supported by native VTML.
        $xml = [xml]('<root>' + $body.Replace('<br>', '<br/>') + '</root>')
        foreach ($node in $xml.SelectNodes('//*')) {
            if ($node.Name -notin @('root', 'strong', 'br', 'a')) { throw "Unsupported handbook markup: $($node.Name)" }
        }
        $links = @($xml.SelectNodes('//a') | ForEach-Object { $_.GetAttribute('href').Replace('handbook://', '') })
        foreach ($link in $links) { if ($link -cnotin $codes) { throw "Broken handbook link: $link" } }
        if ($page.pageCode -eq 'vsslssmod:start' -and @($codes | Where-Object { $_ -ne 'vsslssmod:start' -and $_ -cnotin $links }).Count -gt 0) { throw 'Overview does not link every chapter.' }
        if ($page.pageCode -ne 'vsslssmod:start' -and 'vsslssmod:start' -cnotin $links) { throw 'Missing overview return link.' }
    }
}
Write-Host "PASS $($pages.Count) native handbook pages: bilingual references, categories, supported markup and chapter links."
