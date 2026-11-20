#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$entryCount = 0

function Read-Language([string]$Path) {
    $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $entries = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Value.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw "Non-string translation: $Path / $($property.Name)" }
            $value = $property.Value.GetString()
            if ([string]::IsNullOrWhiteSpace($value) -or -not $entries.TryAdd($property.Name, $value)) {
                throw "Empty or duplicate translation: $Path / $($property.Name)"
            }
            [void][Text.CompositeFormat]::Parse($value)
        }
        return ,$entries
    }
    finally { $document.Dispose() }
}

foreach ($domain in @('vsslssmod', 'vsslssmoddebug', 'game')) {
    $languagePath = Join-Path $workspacePath "assets/$domain/lang"
    $english = Read-Language (Join-Path $languagePath 'en.json')
    $chinese = Read-Language (Join-Path $languagePath 'zh-cn.json')
    $differences = @(Compare-Object @($english.Keys) @($chinese.Keys))
    if ($differences.Count -ne 0) { throw "Language keys differ for $domain : $($differences | Out-String)" }
    foreach ($key in $english.Keys) {
        $enArguments = @([regex]::Matches($english[$key], '(?<!\{)\{(\d+)(?:[,}:])') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        $zhArguments = @([regex]::Matches($chinese[$key], '(?<!\{)\{(\d+)(?:[,}:])') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        if (($enArguments -join ',') -cne ($zhArguments -join ',')) { throw "Translation placeholders differ: $domain : $key" }
        foreach ($language in @($english, $chinese)) {
            $format = [Text.CompositeFormat]::Parse($language[$key])
            $arguments = [object[]]::new($format.MinimumArgumentCount)
            for ($i = 0; $i -lt $arguments.Length; $i++) { $arguments[$i] = [double]12.5 }
            $rendered = [string]::Format([Globalization.CultureInfo]::InvariantCulture, $format, $arguments)
            if ($rendered -match '(?<!\{)\{\d+(?:[,}:])') { throw "Unreplaced formatted placeholder: $domain : $key" }
        }
        if ($key -eq 'speed-slider-format' -and [regex]::Match($english[$key], '%[-+0 #]*\d*(?:\.\d+)?[a-zA-Z]').Value -cne [regex]::Match($chinese[$key], '%[-+0 #]*\d*(?:\.\d+)?[a-zA-Z]').Value) {
            throw 'The native slider format must retain its numeric conversion.'
        }
        $entryCount++
    }
    if ($domain -eq 'vsslssmod') {
        # Server snapshots localize through L(player, key, ...), without the
        # domain prefix used by client calls. Check these literal keys too.
        foreach ($sourceFile in Get-ChildItem -LiteralPath (Join-Path $workspacePath 'src/Seraph/Server') -Filter '*.cs' -File) {
            $source = [IO.File]::ReadAllText($sourceFile.FullName)
            foreach ($call in [regex]::Matches($source, '\bL\([^,\r\n]+,\s*"([a-z][a-z0-9-]+)"\s*[,)]')) {
                $key = $call.Groups[1].Value
                if (-not $english.ContainsKey($key)) { throw "Missing server snapshot translation: $key ($($sourceFile.Name))" }
            }
        }
        $panel = [IO.File]::ReadAllText((Join-Path $workspacePath 'src/Seraph/Client/CharacterMetricsPanel.cs'))
        foreach ($array in [regex]::Matches($panel, '(?s)string\[\]\s+\w+Keys\s*=\s*\[(.*?)\];')) {
            foreach ($member in [regex]::Matches($array.Groups[1].Value, '"([a-z]+)"')) {
                foreach ($prefix in @('metric-', 'metric-help-')) {
                    $key = $prefix + $member.Groups[1].Value
                    if (-not $english.ContainsKey($key)) { throw "Missing dynamic panel key: $key" }
                }
            }
        }
        $exertion = [IO.File]::ReadAllText((Join-Path $workspacePath 'src/Seraph.Core/Framework/Exertion.cs'))
        $members = [regex]::Match($exertion, '(?s)enum ExertionKind\s*\{([^}]+)').Groups[1].Value -split ','
        foreach ($member in $members) {
            $key = 'activity-' + $member.Trim().ToLowerInvariant()
            if (-not $english.ContainsKey($key)) { throw "Missing dynamic activity key: $key" }
        }
        $stages = [regex]::Match($exertion, '(?s)enum RecoveryStage\s*\{([^}]+)').Groups[1].Value -split ','
        foreach ($stage in $stages) {
            $key = 'recovery-stage-' + $stage.Trim().ToLowerInvariant()
            if (-not $english.ContainsKey($key)) { throw "Missing dynamic recovery key: $key" }
        }
        $statusSource = [IO.File]::ReadAllText((Join-Path $workspacePath 'src/Seraph/Client/StatusIconsHud.cs'))
        $statusKeys = [regex]::Match($statusSource, '(?s)string\[\]\s+IconKeys\s*=\s*\[(.*?)\];')
        if (-not $statusKeys.Success) { throw 'Status icon keys could not be enumerated.' }
        foreach ($member in [regex]::Matches($statusKeys.Groups[1].Value, '"([a-z-]+)"')) {
            foreach ($prefix in @('status-icon-', 'status-icon-help-')) {
                $key = $prefix + $member.Groups[1].Value
                if (-not $english.ContainsKey($key)) { throw "Missing dynamic status icon key: $key" }
            }
        }
        $clinicalSource = [IO.File]::ReadAllText((Join-Path $workspacePath 'src/Seraph.Core/Framework/ClinicalEffects.cs'))
        $clinicalIds = @([regex]::Matches($clinicalSource, 'Add\("([a-z-]+)"') | ForEach-Object { $_.Groups[1].Value })
        foreach ($key in @($clinicalIds) + @('blood-toxicity', 'lactate-overload', 'vitaminc-deficiency', 'overexertion-stamina', 'overexertion-lactate')) {
            foreach ($prefix in @('effect-', 'effect-help-')) {
                if (-not $english.ContainsKey($prefix + $key)) { throw "Missing dynamic effect key: $prefix$key" }
            }
        }
    }
}
Write-Host "Localization assets: $entryCount matching keys; JSON, duplicate keys and placeholders validated."
