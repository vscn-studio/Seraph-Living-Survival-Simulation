[CmdletBinding()]
param([string]$ModsPath = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$allowed = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods'
if ([IO.Path]::GetFullPath($ModsPath).TrimEnd('\') -ine $allowed) { throw 'Release deployment is restricted to 测试服务器.' }
$packages = Join-Path $root 'artifacts'
$manifest = Get-Content -LiteralPath (Join-Path $packages 'build-manifest.json') -Raw | ConvertFrom-Json
$version = (Get-Content -LiteralPath (Join-Path $root 'modinfo.json') -Raw | ConvertFrom-Json).version
if ($manifest.packageVersion -cne $version -or $manifest.packages.Count -ne 2) { throw 'Invalid release manifest.' }
function Read-Metadata([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entries = @($zip.Entries | Where-Object FullName -ceq 'modinfo.json')
        if ($entries.Count -ne 1) { return $null }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}
# Validate both complete inputs before touching any installed package.
$inputs = foreach ($id in @('vsslssmod','vsslssmoddebug')) {
    $name = "$id-$version.zip"
    $path = Join-Path $packages $name
    $record = @($manifest.packages | Where-Object file -ceq $name)
    if ($record.Count -ne 1 -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release input: $name" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($hash -ine $record[0].sha256) { throw "Release hash mismatch: $name" }
    $metadata = Read-Metadata $path
    if ($metadata.modid -cne $id -or $metadata.version -cne $version) { throw "Release metadata mismatch: $name" }
    if ($id -eq 'vsslssmoddebug' -and $metadata.dependencies.vsslssmod -cne $version) { throw 'Debug dependency mismatch.' }
    [pscustomobject]@{ Name=$name; Path=$path; Hash=$hash; Id=$id }
}
New-Item -ItemType Directory -Path $ModsPath -Force | Out-Null
$resolvedMods = (Resolve-Path -LiteralPath $ModsPath).Path
$old = @(Get-ChildItem -LiteralPath $resolvedMods -File -Filter '*.zip' | Where-Object {
    $metadata = Read-Metadata $_.FullName
    $null -ne $metadata -and $metadata.modid -cin @('vsslssmod','vsslssmoddebug')
})
$backupRoot = [IO.Path]::GetTempPath()
$backup = Join-Path $backupRoot ('Seraph-deploy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$moved = @(); $copied = @(); $verified = $false
try {
    foreach ($file in $old) {
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $backup $file.Name)
        $moved += $file.Name
    }
    foreach ($releasePackage in $inputs) {
        $destination = Join-Path $resolvedMods $releasePackage.Name
        Copy-Item -LiteralPath $releasePackage.Path -Destination $destination
        $copied += $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $releasePackage.Hash) { throw "Deployed hash mismatch: $($releasePackage.Name)" }
    }
    foreach ($releasePackage in $inputs) {
        $installed = @(Get-ChildItem -LiteralPath $resolvedMods -File -Filter '*.zip' | Where-Object { (Read-Metadata $_.FullName).modid -ceq $releasePackage.Id })
        if ($installed.Count -ne 1 -or $installed[0].Name -cne $releasePackage.Name) { throw "Duplicate installed package: $($releasePackage.Id)" }
        Write-Host "Verified $($releasePackage.Name) SHA256 $($releasePackage.Hash.ToLowerInvariant())"
    }
    $verified = $true
} catch {
    foreach ($file in $copied) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force } }
    foreach ($name in $moved) { Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $resolvedMods $name) }
    throw
} finally {
    $resolvedBackup = [IO.Path]::GetFullPath($backup)
    $backupPrefix = [IO.Path]::GetFullPath($backupRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar + 'Seraph-deploy-'
    if (-not $resolvedBackup.StartsWith($backupPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe backup cleanup path.' }
    if (@(Get-ChildItem -LiteralPath $resolvedBackup -File).Count -eq 0 -or $verified) {
        Remove-Item -LiteralPath $resolvedBackup -Recurse -Force
    }
}

