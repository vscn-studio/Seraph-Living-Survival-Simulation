#Requires -Version 7.0
<#
.SYNOPSIS
Runs an isolated, managed Vintage Story 1.22.x package smoke test.
.DESCRIPTION
Creates fresh data, world, logs and mods under the OS temporary Seraph-smoke/runs/<runId> directory.
Checks the game's Mods directory contains only the known base-game files, records
their hashes, and never installs into it. No previous world or configuration is reused.
stdout/stderr are drained asynchronously; stdin stays open until explicit /stop.
This verifies package initialization and console configuration, not player or UI flows.
.EXAMPLE
pwsh -NoProfile -File scripts/Smoke-Server.ps1
.EXAMPLE
pwsh -NoProfile -File scripts/Smoke-Server.ps1 -GamePath E:/games/vs/VintagestoryServer.dll
.EXAMPLE
pwsh -NoProfile -File scripts/Smoke-Server.ps1 -NativeLibraryPath E:/games/vs-windows/Lib
#>
[CmdletBinding()]
param(
    [string] $GamePath = 'E:/vintagestory/vs_client_linux-x64_1.22.7/vintagestory/VintagestoryServer.dll',
    [string] $ExpectedGameVersion = '1.22.7',
    [string] $NativeLibraryPath = '',
    [string] $MainPackage = '',
    [string[]] $ExtraPackages = @(),
    [ValidateRange(1024, 65535)]
    [int] $Port = 42431,
    [ValidateRange(30, 600)]
    [int] $StartupTimeoutSeconds = 120,
    [ValidateRange(0, 600)]
    [int] $ObservationSeconds = 75
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$smokePath = Join-Path $temporaryRoot 'Seraph-smoke'
$temporaryPrefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $smokePath.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Smoke output directory is outside the OS temporary directory.'
}
if ([string]::IsNullOrWhiteSpace($MainPackage)) {
    $packageVersion = (Get-Content -LiteralPath (Join-Path $workspacePath 'modinfo.json') -Raw | ConvertFrom-Json).version
    $MainPackage = Join-Path $workspacePath "artifacts/vsslssmod-$packageVersion.zip"
}
$GamePath = [IO.Path]::GetFullPath($GamePath)
$MainPackage = [IO.Path]::GetFullPath($MainPackage)
$ownerPath = Join-Path $smokePath ("child-owner-$Port.json")
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N')
$evidencePath = Join-Path $smokePath ('runs/' + $runId)
if (Test-Path -LiteralPath $evidencePath) { throw ('Run directory already exists: ' + $evidencePath) }
$dataPath = Join-Path $evidencePath 'data'
$modsPath = Join-Path $evidencePath 'mods'
$logsPath = Join-Path $dataPath 'Logs'
$configPath = Join-Path $dataPath 'ModConfig/vsslssmod.Server.json'
$gameDirectory = [IO.Path]::GetDirectoryName($GamePath)
$gameModsPath = Join-Path $gameDirectory 'Mods'
[void][IO.Directory]::CreateDirectory($evidencePath)
[IO.File]::WriteAllText((Join-Path $evidencePath 'stdout.log'), '')
[IO.File]::WriteAllText((Join-Path $evidencePath 'stderr.log'), '')

$result = [ordered]@{
    SchemaVersion = 1
    RunId = $runId
    StartUtc = [DateTimeOffset]::UtcNow.ToString('O')
    EndUtc = $null
    GamePath = $GamePath
    ExpectedGameVersion = $ExpectedGameVersion
    StartupTimeoutSeconds = $StartupTimeoutSeconds
    NativeLibraryPath = $NativeLibraryPath
    NativeLibraryFiles = @()
    PackagePath = $MainPackage
    PackageSha256 = $null
    DataPath = $dataPath
    ModsPath = $modsPath
    FreshDataDirectory = $true
    GameModsPath = $gameModsPath
    GameModsFiles = @()
    BindAddress = '127.0.0.1'
    Port = $Port
    ProcessId = $null
    Arguments = @()
    ExitCode = $null
    StopRequestedUtc = $null
    StopCommandWritten = $false
    CleanShutdown = $false
    ForcedKill = $false
    Passed = $false
    Failure = $null
    EvidencePath = $evidencePath
    Checks = [Collections.Generic.List[object]]::new()
}
$runState = @{
    Child = $null
    StdoutTask = $null
    StderrTask = $null
    Lock = $null
    Started = $false
    AllStagesCompleted = $false
    StopTimedOut = $false
    LogOffsets = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::OrdinalIgnoreCase)
    NewLogs = [Text.StringBuilder]::new()
}

function Add-SmokeCheck([string] $Name, [bool] $Passed, [string] $Detail) {
    $result.Checks.Add([ordered]@{
        Name = $Name
        Passed = $Passed
        Utc = [DateTimeOffset]::UtcNow.ToString('O')
        Detail = $Detail
    })
    $state = if ($Passed) { 'PASS' } else { 'FAIL' }
    Write-Host ('[{0}] {1}: {2}' -f $state, $Name, $Detail)
}

function Update-SmokeLogs {
    if (-not [IO.Directory]::Exists($logsPath)) { return }
    # Read only active log names. Newly created rotated/archive files could otherwise
    # reintroduce historical command responses into this run's observation buffer.
    foreach ($fileName in @('server-main.log', 'server-debug.log')) {
        $path = Join-Path $logsPath $fileName
        if (-not [IO.File]::Exists($path)) { continue }
        $stream = $null
        try {
            $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $offset = if ($runState.LogOffsets.ContainsKey($path)) { $runState.LogOffsets[$path] } else { 0L }
            # Every run starts at byte zero in fresh logs. Also handle truncation within a run.
            if ($stream.Length -lt $offset) { $offset = 0L }
            [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
            $buffer = [byte[]]::new(65536)
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                [void]$runState.NewLogs.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $read))
            }
            $runState.LogOffsets[$path] = $stream.Position
        }
        catch [IO.IOException] {
            # A logger may briefly replace or lock a file. The next poll retries its cursor.
        }
        finally {
            if ($null -ne $stream) { $stream.Dispose() }
        }
    }
}

function Wait-SmokeLog([string] $Name, [string] $Pattern, [int] $FromOffset = 0, [int] $TimeoutSeconds = 20) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $nextProgress = 0.0
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Update-SmokeLogs
        $newText = $runState.NewLogs.ToString($FromOffset, $runState.NewLogs.Length - $FromOffset)
        if ([Text.RegularExpressions.Regex]::IsMatch($newText, $Pattern,
                [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::Multiline,
                [TimeSpan]::FromSeconds(2))) {
            Add-SmokeCheck $Name $true ('Expected new-run log evidence found after {0:N1}s.' -f $watch.Elapsed.TotalSeconds)
            return
        }
        if ($runState.Child.HasExited) {
            Add-SmokeCheck $Name $false ('Child exited early with code ' + $runState.Child.ExitCode)
            throw ('Server exited before check: ' + $Name)
        }
        if ($watch.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Host ('[WAIT] {0}: {1:N0}/{2}s; child PID {3}.' -f $Name, $watch.Elapsed.TotalSeconds, $TimeoutSeconds, $runState.Child.Id)
            $nextProgress += 5
        }
        Start-Sleep -Milliseconds 250
    }
    Add-SmokeCheck $Name $false ('No matching new-run evidence within ' + $TimeoutSeconds + ' seconds.')
    throw ('Timed out waiting for ' + $Name)
}

function Send-SmokeCommand([string] $Command) {
    Update-SmokeLogs
    if ($runState.Child.HasExited) { throw ('Cannot send command; child exited: ' + $Command) }
    $offset = $runState.NewLogs.Length
    Write-Host ('[SEND] ' + $Command)
    $runState.Child.StandardInput.WriteLine($Command)
    $runState.Child.StandardInput.Flush()
    return $offset
}

function Read-SmokeConfig {
    if (-not [IO.File]::Exists($configPath)) { return $null }
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.FileStream]::new($configPath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $reader = [IO.StreamReader]::new($stream)
        $config = $reader.ReadToEnd() | ConvertFrom-Json
        if ($config.Format -ne 'seraph.release.config' -or $config.ConfigVersion -ne 1 -or
            $config.SleepPressureRiseSeconds -ne 90000 -or $config.SleepPressureRecoverySeconds -ne 28800 -or
            $config.FatigueRiseSeconds -ne 172800 -or $config.FatigueSleepRecoverySeconds -ne 28800 -or
            [Math]::Abs($config.FatigueSeatedRecoverySeconds - 28800 / .385) -gt 1e-9) { return $null }
        $property = $config.PSObject.Properties['PhysiologySpeed']
        if ($null -eq $property -or $null -eq $property.Value) { return $null }
        $speed = [double]$property.Value
        if (-not [double]::IsFinite($speed) -or $speed -lt 0.1 -or $speed -gt 20) { return $null }
        return $speed
    }
    catch { return $null } # Config replacement is transient; command checks have a bounded retry.
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function Wait-SmokeConfig([string] $Name, [Nullable[double]] $Expected = $null) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        $speed = Read-SmokeConfig
        if ($null -ne $speed -and ($null -eq $Expected -or [Math]::Abs($speed - $Expected) -lt 1e-9)) {
            Add-SmokeCheck $Name $true ('Release config v1, calendar pacing 25/8/48/8/(8/0.385) hours; legacy PhysiologySpeed = ' + $speed + ' in ' + $configPath)
            return [double]$speed
        }
        if ($runState.Child.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    Add-SmokeCheck $Name $false ('Expected release config v1 with current calendar defaults and legacy PhysiologySpeed ' + $Expected)
    throw ('Configuration check failed: ' + $Name)
}

function Stop-SmokeChild {
    if ($runState.Child.HasExited) { return }
    if ($null -eq $result.StopRequestedUtc) {
        $result.StopRequestedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-Host '[SEND] /stop (stdin remains open until the child exits).'
        try {
            $runState.Child.StandardInput.WriteLine('/stop')
            $runState.Child.StandardInput.Flush()
            $result.StopCommandWritten = $true
        }
        catch {
            # A closed pipe may mean shutdown is already in progress. Still grant the
            # child the full bounded stop interval before any forced termination.
            Write-Host ('[WAIT] Could not write /stop; waiting for own child: ' + $_.Exception.Message)
        }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $nextProgress = 0.0
    while ($watch.Elapsed.TotalSeconds -lt 45) {
        if ($runState.Child.WaitForExit(250)) {
            Update-SmokeLogs
            $result.ExitCode = $runState.Child.ExitCode
            $result.CleanShutdown = ($result.ExitCode -eq 0 -and $result.StopCommandWritten)
            Add-SmokeCheck 'explicit-stop-exit-zero' $result.CleanShutdown ('Exit code ' + $result.ExitCode)
            return
        }
        Update-SmokeLogs
        if ($watch.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Host ('[WAIT] Saving and stopping own child: {0:N0}/45s.' -f $watch.Elapsed.TotalSeconds)
            $nextProgress += 5
        }
    }
    $runState.StopTimedOut = $true
    Add-SmokeCheck 'explicit-stop-exit-zero' $false 'The child did not stop within 45 seconds.'
    throw 'Explicit /stop timed out.'
}

try {
    # FileShare.None serializes instances using the same port. Each run has its own
    # data/world directory; distinct loopback ports can check two packs independently.
    # A persisted owner also detects an orphan
    # if an earlier PowerShell host was forcibly terminated before its finally block.
    $runState.Lock = [IO.FileStream]::new((Join-Path $smokePath ("run-$Port.lock")), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if ([IO.File]::Exists($ownerPath)) {
        $previousOwner = [IO.File]::ReadAllText($ownerPath) | ConvertFrom-Json
        if ($null -ne $previousOwner.PSObject.Properties['ProcessId'] -and $null -ne $previousOwner.ProcessId) {
            $previousChild = $null
            try { $previousChild = [Diagnostics.Process]::GetProcessById([int]$previousOwner.ProcessId) }
            catch [ArgumentException] { } # PID no longer exists.
            if ($null -ne $previousChild) {
                try {
                    $recordedStart = [DateTimeOffset]::Parse($previousOwner.ProcessStartUtc)
                    $actualStart = [DateTimeOffset]$previousChild.StartTime.ToUniversalTime()
                    if ([Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -lt 2) {
                        throw ('A previous smoke child may still own the smoke port: PID ' + $previousOwner.ProcessId + '. Stop it explicitly before retrying.')
                    }
                }
                finally { $previousChild.Dispose() }
            }
        }
    }
    Add-SmokeCheck 'exclusive-smoke-directory' $true 'Exclusive lock acquired; no recorded live child owns the smoke port.'
    if (-not [IO.File]::Exists($GamePath)) { throw ('Server DLL not found: ' + $GamePath) }
    if (-not [IO.File]::Exists($MainPackage)) { throw ('Main ZIP not found: ' + $MainPackage) }
    $result.PackageSha256 = (Get-FileHash -LiteralPath $MainPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    # --addModPath is additive: a fresh data directory alone does not isolate game/Mods.
    # These names are verified in the local 1.22.3 Windows and 1.22.7 Linux distributions.
    $baseGameDlls = @('VSCreativeMod.dll', 'VSEssentials.dll', 'VSSurvivalMod.dll')
    $allowedGameFiles = $baseGameDlls + @('VSCreativeMod.pdb', 'VSEssentials.pdb',
        'VSSurvivalMod.pdb', 'do_not_add_mods_here.txt')
    if (-not [IO.Directory]::Exists($gameModsPath)) { throw ('Base-game Mods directory not found: ' + $gameModsPath) }
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($gameModsPath) | Sort-Object) {
        $name = [IO.Path]::GetFileName($entry)
        if ([IO.Directory]::Exists($entry) -or $name -notin $allowedGameFiles) {
            throw ('Unexpected entry in the game Mods directory; refusing to load third-party mods: ' + $entry)
        }
        $result.GameModsFiles += [ordered]@{
            Name = $name
            Path = $entry
            Sha256 = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    foreach ($name in $baseGameDlls) {
        if (-not [IO.File]::Exists((Join-Path $gameModsPath $name))) {
            throw ('Required base-game DLL not found: ' + (Join-Path $gameModsPath $name))
        }
    }
    Add-SmokeCheck 'base-game-mods-only' $true ('Only verified base-game filenames in ' + $gameModsPath + '; SHA-256 recorded for every file.')
    if ((Test-Path -LiteralPath $dataPath) -or (Test-Path -LiteralPath $modsPath)) {
        throw 'Fresh run data or mods directory already exists; refusing to reuse configuration, logs or worlds.'
    }
    [void][IO.Directory]::CreateDirectory($dataPath)
    [void][IO.Directory]::CreateDirectory($modsPath)
    Add-SmokeCheck 'fresh-run-data' $true ('New empty data and mods directories under ' + $evidencePath)
    $copiedPackage = Join-Path $modsPath ([IO.Path]::GetFileName($MainPackage))
    [IO.File]::Copy($MainPackage, $copiedPackage, $false)
    $copiedHash = (Get-FileHash -LiteralPath $copiedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copiedHash -ne $result.PackageSha256) { throw 'Main ZIP changed during the copy; copied package hash differs.' }
    foreach ($extraPackage in $ExtraPackages) {
        $extraPath = [IO.Path]::GetFullPath($extraPackage)
        [IO.File]::Copy($extraPath, (Join-Path $modsPath ([IO.Path]::GetFileName($extraPath))), $false)
    }
    Add-SmokeCheck 'isolated-main-package' $true ('Copied main package and ' + $ExtraPackages.Count + ' companion package(s); game Mods was not changed.')

    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnet.Source
    $start.WorkingDirectory = $gameDirectory
    $start.CreateNoWindow = $true
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    # Allow a managed Linux distribution to use Windows native libraries without
    # copying into the game install or changing the parent process environment.
    if (-not [string]::IsNullOrWhiteSpace($NativeLibraryPath)) {
        $nativePath = (Resolve-Path -LiteralPath $NativeLibraryPath -ErrorAction Stop).Path
        if (-not [IO.Directory]::Exists($nativePath)) { throw 'NativeLibraryPath must be a directory.' }
        $start.Environment['PATH'] = $nativePath + [IO.Path]::PathSeparator + $start.Environment['PATH']
        $result.NativeLibraryPath = $nativePath
        foreach ($name in @('libzstd.dll', 'e_sqlite3.dll')) {
            $nativeFile = Join-Path $nativePath $name
            if ([IO.File]::Exists($nativeFile)) {
                $result.NativeLibraryFiles += [ordered]@{
                    Name = $name
                    Sha256 = (Get-FileHash -LiteralPath $nativeFile -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        }
    }
    $arguments = @($GamePath, '--dataPath', $dataPath, '--addModPath', $modsPath,
        '--ip', '127.0.0.1', '--port', [string]$Port)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $result.Arguments = $arguments
    $runState.Child = [Diagnostics.Process]::new()
    $runState.Child.StartInfo = $start
    if (-not $runState.Child.Start()) { throw 'Could not start the dedicated server child.' }
    $runState.Started = $true
    $result.ProcessId = $runState.Child.Id
    # Start both readers immediately. Unlike synchronous ReadToEnd, neither pipe can fill
    # while the main thread polls engine logs or writes console commands.
    $runState.StdoutTask = $runState.Child.StandardOutput.ReadToEndAsync()
    $runState.StderrTask = $runState.Child.StandardError.ReadToEndAsync()
    $runState.Child.StandardInput.AutoFlush = $true
    [IO.File]::WriteAllText($ownerPath, ([ordered]@{
        RunId = $runId
        ProcessId = $runState.Child.Id
        ProcessStartUtc = $runState.Child.StartTime.ToUniversalTime().ToString('O')
        GamePath = $GamePath
        DataPath = $dataPath
    } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host ('[START] PID {0}; evidence {1}' -f $runState.Child.Id, $evidencePath)

    $readyPattern = '(?s)\A(?=.*Entering runphase RunGame)(?=.*Dedicated Server now running on Port ' + $Port + ' and ip 127\.0\.0\.1)(?=.*\[vsslssmod\][^\r\n]*VS 1\.22\.)(?=.*\[vsslssmod\]\s+(?:shadow|survival) mode\.)'
    Wait-SmokeLog 'server-ready-and-main-mod-initialized' $readyPattern -TimeoutSeconds $StartupTimeoutSeconds
    $versionPattern = '\[vsslssmod\][^\r\n]*VS ' + [regex]::Escape($ExpectedGameVersion) + '\.'
    $versionMatches = [regex]::IsMatch($runState.NewLogs.ToString(), $versionPattern)
    Add-SmokeCheck 'expected-game-version' $versionMatches ('Required VS ' + $ExpectedGameVersion)
    if (-not $versionMatches) { throw 'Loaded game version does not match the requested validation target.' }
    $dependenciesLoaded = -not [regex]::IsMatch($runState.NewLogs.ToString(), '(?i)Could not resolve some dependencies|Version mismatch \(has')
    Add-SmokeCheck 'companion-dependencies-loaded' $dependenciesLoaded 'No unresolved or mismatched mod dependencies in the new-run logs.'
    if (-not $dependenciesLoaded) { throw 'One or more companion mods were excluded due to unresolved dependencies.' }
    if (@($ExtraPackages | Where-Object { [IO.Path]::GetFileName($_) -like 'vsshmod-*' }).Count -gt 0) {
        Wait-SmokeLog 'herbalism-assets-loaded' '\[vsshmod\] Loaded 24 plants, \d+ processing recipes\.'
    }
    $baselineSpeed = Wait-SmokeConfig 'initial-server-configuration'

    $offset = Send-SmokeCommand '/vsslssmod'
    Wait-SmokeLog 'status-command' 'Seraph\s+\S+\s*\|\s*(?:shadow|survival)\s*\|' $offset
    $offset = Send-SmokeCommand '/vsslssmod speed 1.5'
    Wait-SmokeLog 'retired-physiology-speed-command' 'Physiology follows the game calendar\. Use /time speed' $offset
    [void](Wait-SmokeConfig 'retired-command-keeps-persisted-config' $baselineSpeed)
    # Keep the world running long enough to exercise periodic source refresh and
    # column lifecycle callbacks, rather than checking initialization alone.
    $observation = [Diagnostics.Stopwatch]::StartNew()
    $nextObservationProgress = 0.0
    while ($observation.Elapsed.TotalSeconds -lt $ObservationSeconds) {
        if ($runState.Child.HasExited) { throw 'Server exited during runtime observation.' }
        Update-SmokeLogs
        if ($observation.Elapsed.TotalSeconds -ge $nextObservationProgress) {
            Write-Host ('[WAIT] Runtime observation: {0:N0}/{1}s.' -f $observation.Elapsed.TotalSeconds, $ObservationSeconds)
            $nextObservationProgress += 10
        }
        Start-Sleep -Milliseconds 250
    }
    Add-SmokeCheck 'periodic-runtime-observation' $true ('Observed the running world for ' + $ObservationSeconds + ' seconds.')
    Stop-SmokeChild
    if (-not $result.CleanShutdown) { throw 'Server did not finish a clean explicit shutdown.' }
    Update-SmokeLogs
    $modErrorPattern = '(?im)(?:\[Error\]|\[Fatal\])[^\r\n]*(?:vsslssmod|vsshmod|Seraph)|^\s+at Seraph\.|\[vsslssmod\] Pollution capture deferred'
    $noModErrors = -not [Text.RegularExpressions.Regex]::IsMatch($runState.NewLogs.ToString(), $modErrorPattern)
    Add-SmokeCheck 'no-mod-runtime-errors' $noModErrors 'Checked only newly emitted logs for mod errors or Seraph namespace exception frames.'
    if (-not $noModErrors) { throw 'A mod runtime error was present in the new-run logs.' }
    $runState.AllStagesCompleted = $true
}
catch {
    $result.Failure = $_.Exception.Message
    Write-Host ('[FAIL] ' + $result.Failure)
}
finally {
    if ($null -ne $runState.Child -and $runState.Started) {
        try {
            # Do not close stdin to stop the server. Failure and interruption paths also
            # request /stop and wait, unless that same stop has already timed out.
            if (-not $runState.Child.HasExited -and -not $runState.StopTimedOut) {
                try { Stop-SmokeChild }
                catch {
                    if ($null -eq $result.Failure) { $result.Failure = $_.Exception.Message }
                }
            }
            if (-not $runState.Child.HasExited) {
                $result.ForcedKill = $true
                $result.CleanShutdown = $false
                if ($null -eq $result.Failure) { $result.Failure = 'Own child required forced termination after cleanup failed.' }
                # This exact Process object is the child created above. Never kill a PID
                # from the owner file, a port lookup, or a process-name enumeration.
                $runState.Child.Kill()
                [void]$runState.Child.WaitForExit(10000)
            }
            if ($runState.Child.HasExited) {
                $result.ExitCode = $runState.Child.ExitCode
                $runState.Child.StandardInput.Close()
            }
            foreach ($streamName in @('Stdout', 'Stderr')) {
                $task = $runState[$streamName + 'Task']
                $capturePath = Join-Path $evidencePath ($streamName.ToLowerInvariant() + '.log')
                $captured = ''
                if ($null -ne $task) {
                    if (-not $task.Wait(5000)) { throw ($streamName + ' capture did not finish after child shutdown.') }
                    $captured = $task.GetAwaiter().GetResult()
                }
                [IO.File]::WriteAllText($capturePath, $captured, [Text.UTF8Encoding]::new($false))
            }
            Update-SmokeLogs
        }
        catch {
            $result.CleanShutdown = $false
            if ($null -eq $result.Failure) { $result.Failure = 'Cleanup/capture failed: ' + $_.Exception.Message }
        }
        finally { $runState.Child.Dispose() }
    }
    else {
        if ($null -ne $runState.Child) { $runState.Child.Dispose() }
        [IO.File]::WriteAllText((Join-Path $evidencePath 'stdout.log'), '')
        [IO.File]::WriteAllText((Join-Path $evidencePath 'stderr.log'), '')
    }
    if (-not $runState.AllStagesCompleted -and $null -eq $result.Failure) {
        $result.Failure = 'The run did not complete all smoke stages; an interrupted run is not a clean pass.'
    }
    $result.EndUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $allChecksPassed = $result.Checks.Count -gt 0 -and @($result.Checks | Where-Object { -not $_.Passed }).Count -eq 0
    $result.Passed = $null -eq $result.Failure -and $allChecksPassed -and $result.CleanShutdown -and -not $result.ForcedKill -and $result.ExitCode -eq 0
    try {
        [IO.File]::WriteAllText((Join-Path $evidencePath 'new-server-logs.log'), $runState.NewLogs.ToString(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $evidencePath 'result.json'), ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    }
    finally {
        if ($null -ne $runState.Lock) { $runState.Lock.Dispose() }
    }
}

Write-Host ('[RESULT] {0}; {1}' -f $(if ($result.Passed) { 'PASS' } else { 'FAIL' }), (Join-Path $evidencePath 'result.json'))
if (-not $result.Passed) { exit 1 }
exit 0
