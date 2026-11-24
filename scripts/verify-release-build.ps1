[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Release', 'Debug')][string]$Configuration,
    [Parameter(Mandatory)][ValidatePattern('\Av\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z')][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$ExpectedCommit,
    [Parameter(Mandatory)][string]$ReferenceDirectory,
    [Parameter(Mandatory)][string]$YmmpxArchive,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
$referenceDirectory = (Get-Item -LiteralPath $ReferenceDirectory).FullName
$ymmpxArchive = (Get-Item -LiteralPath $YmmpxArchive).FullName
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new verification output directory.' }
if ($output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $output -eq $repo) { throw 'Choose a verification directory outside the repository.' }

New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = Join-Path $output 'build'
$runRoot = Join-Path $output 't'
$candidate = Join-Path $output 'candidate'
New-Item -ItemType Directory -Path $runRoot, $candidate | Out-Null

function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '\A[0-9a-f]{64}\z' -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "SHA-256 mismatch: $(Split-Path -Leaf $Path)"
    }
}

function Assert-Checkout {
    $commit = git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -ne $ExpectedCommit) {
        throw 'The checkout does not match the requested commit.'
    }
    $changed = git -C $repo status --porcelain --untracked-files=all --ignored=matching
    if ($LASTEXITCODE -ne 0 -or $changed) {
        throw 'Verification requires a clean checkout, including untracked and ignored files.'
    }
}

$metadataPath = Join-Path $referenceDirectory 'YMM4-reference.json'
$metadataHash = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
$references = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$referencePins = @{
    'YukkuriMovieMaker.Plugin.dll' = '4B64AE98A97F8FC11C374DD3409CEF368B9B3D15059DF031DBD450C2BDEBC3C0'
    'YukkuriMovieMaker.dll' = '20485A97F2C5717446F0834CE35131DA13BD2F2135E7F51EA63385A211CA5C2A'
    'YukkuriMovieMaker.Controls.dll' = '720F1B1D9BE22233B582CDBEE8CEDC5378B6FCEA45D3367922FDB516F9C2AD6B'
}
if ($references.release -cne 'v4.57.0.1' -or
    $references.source -cne 'https://github.com/manju-summoner/YukkuriMovieMaker4/releases/download/v4.57.0.1/YukkuriMovieMaker_v4.57.0.1_Lite.zip' -or
    $references.archiveSha256 -ne '47837E0AA514533BDEB3D018FF28FBAC8D2A57E8D6A5665DDFB72E9329EC0553' -or
    $references.files.Count -ne $referencePins.Count) { throw 'Unexpected YMM4 reference archive.' }
foreach ($name in $referencePins.Keys) {
    $reference = @($references.files | Where-Object name -CEQ $name)
    if ($reference.Count -ne 1 -or $reference[0].sha256 -ne $referencePins[$name]) {
        throw "Unexpected reference metadata: $name"
    }
    $path = Join-Path $referenceDirectory $name
    Assert-Hash $path $referencePins[$name]
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion -ne $reference[0].fileVersion) {
        throw "Unexpected reference version: $name"
    }
}

$ymmpxArchiveSha = '9D5EE82C96D6A5ED57D805306CFE037A6669F0D3D15D6C28919E1A59158680DA'
$ymmpxDllSha = '9D6E0A5F1A6FE096098CE77F2FD7A970A83A5C7F0476844E441F003AAA10FE23'
$ymmpxDll = Join-Path $output 'YmmpxLib.dll'
Assert-Hash $ymmpxArchive $ymmpxArchiveSha
$libZip = [IO.Compression.ZipFile]::OpenRead($ymmpxArchive)
try {
    $dllEntries = @($libZip.Entries | Where-Object Name -CEQ 'YmmpxLib.dll')
    if ($dllEntries.Count -ne 1) { throw 'Expected exactly one YmmpxLib.dll in the pinned package.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntries[0], $ymmpxDll)
}
finally {
    $libZip.Dispose()
}
Assert-Hash $ymmpxDll $ymmpxDllSha

$expectedSdk = '10.0.404'
@{ sdk = @{ version = $expectedSdk; rollForward = 'disable' } } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $output 'global.json') -Encoding utf8
Assert-Checkout
Push-Location -LiteralPath $output
try {
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne $expectedSdk) { throw 'The pinned SDK is not active.' }
    dotnet build (Join-Path $repo 'YMMProjectManager.sln') -c $Configuration --artifacts-path $artifacts "-p:Version=$($Tag.Substring(1))" "-p:YMM4Path=$referenceDirectory" -p:YMM4DeployPath= -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "Verification build failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

$pivot = $Configuration.ToLowerInvariant()
$pluginBin = Join-Path $artifacts "bin/YMMProjectManager/$pivot"
$testBin = Join-Path $artifacts "bin/YMMProjectManager.Tests/$pivot"
Copy-Item -Path (Join-Path $testBin '*') -Destination $runRoot -Recurse
$runner = Join-Path $runRoot 'YMMProjectManager.Tests.exe'
$plugin = Join-Path $pluginBin 'YMMProjectManager.dll'
$testedPlugin = Join-Path $runRoot 'YMMProjectManager.dll'
$pluginHash = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
$runnerHash = (Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash
$testAssembly = Join-Path $runRoot 'YMMProjectManager.Tests.dll'
$testAssemblyHash = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
Assert-Hash $testedPlugin $pluginHash
foreach ($reference in $references.files) {
    Assert-Hash (Join-Path $runRoot $reference.name) $reference.sha256
}

function Invoke-Regression([string]$Name, [bool]$WithLibrary, [string]$Filter) {
    $testTemp = Join-Path $output $Name
    New-Item -ItemType Directory -Path $testTemp | Out-Null
    $variables = @{
        TEMP = $testTemp
        TMP = $testTemp
        YMM4_PATH = $referenceDirectory
        YMM4DirPath = $referenceDirectory
        YMM4DeployPath = $null
        YMMPX_LIB_PATH = if ($WithLibrary) { $ymmpxDll } else { $null }
        YMM_TEST_FILTER = $Filter
        YMM_TEST_YMMPX_RELEASED_FIXTURES = $null
        YMM_TEST_YMMPX_PLUGIN_ARCHIVE = $ymmpxArchive
    }
    $oldVariables = @{}
    foreach ($variableName in $variables.Keys) {
        $oldVariables[$variableName] = [Environment]::GetEnvironmentVariable($variableName)
        [Environment]::SetEnvironmentVariable($variableName, $variables[$variableName])
    }

    $log = Join-Path $output "$Name.log"
    Push-Location -LiteralPath $repo
    try {
        & $runner 2>&1 | Tee-Object -FilePath $log | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Regression '$Name' failed ($LASTEXITCODE)." }
    }
    finally {
        Pop-Location
        foreach ($variableName in $oldVariables.Keys) {
            [Environment]::SetEnvironmentVariable($variableName, $oldVariables[$variableName])
        }
    }

    $lines = @(Get-Content -LiteralPath $log)
    $expectedHeader = "Configuration=$Configuration; explicitly configured YmmpxLib present=$WithLibrary"
    $groups = @($lines | Select-String '^Test groups executed: total=(\d+), passed=(\d+), withSkips=(\d+), failed=(\d+), selectedNotRun=(\d+), duration=(\S+)$')
    $skips = @($lines | Select-String '^Explicit conditional skips: (\d+)\.')
    $cases = @($lines | Select-String '^Individually instrumented cases: passed=(\d+), failed=(\d+)')
    if ($lines -cnotcontains $expectedHeader -or $groups.Count -ne 1 -or $skips.Count -ne 1 -or $cases.Count -ne 1) {
        throw "Regression '$Name' did not produce an unambiguous report for its configuration."
    }
    $groupCounts = $groups[0].Matches[0].Groups
    $caseCounts = $cases[0].Matches[0].Groups
    $total = [int]$groupCounts[1].Value
    $passed = [int]$groupCounts[2].Value
    $withSkips = [int]$groupCounts[3].Value
    $skipped = [int]$skips[0].Matches[0].Groups[1].Value
    if ($total -eq 0 -or $total -ne $passed + $withSkips -or $groupCounts[4].Value -ne '0' -or
        $groupCounts[5].Value -ne '0' -or $caseCounts[2].Value -ne '0') {
        throw "Regression '$Name' did not complete its selected groups successfully."
    }
    if ($Filter -and ($total -ne 1 -or $skipped -ne 0 -or -not ($lines -match "^PASS group: $Filter \("))) {
        throw "Required group '$Filter' did not pass."
    }
    $skipPrefix = 'SKIP conditional case: '
    $skipReasons = @($lines -match '^SKIP conditional case: ' | ForEach-Object { $_.Substring($skipPrefix.Length) })
    $allowedSkips = if ($Filter) { @() } else {
        # Optional upstream legacy packages are supplied separately from the hosted candidate job.
        @('v0.2.1', 'v0.3.0', 'v1.0.1' | ForEach-Object { "Released YMMPX $_ fixture unavailable" })
    }
    if (-not $Filter -and -not $WithLibrary) {
        $allowedSkips += @(
            'PF research: YmmpxLib RemoveUiSettings unavailable'
            'real YmmpxLib unavailable'
            'R32 writes a standard-only package with approved names and no overwrite (requires YmmpxLib)'
            'R32 packages native PSD references, PNG sequences and optional UI state (requires YmmpxLib)'
            'R32 matches native PNG selection when the representative is missing or excluded (requires YmmpxLib)'
            'R32 round trips a standard source whose basename resembles old PM v2 (requires YmmpxLib)'
            'R32 checks the exclusion version at publication and pins an existing configuration (requires YmmpxLib)'
            'R23 transfer rejects approved asset replacement and leaves no partial public output (requires YmmpxLib)'
            'R23 transfer cancellation and late target creation preserve output and permit retry (requires YmmpxLib)'
            'R23 transfer compatibility with real YmmpxLib unavailable'
        )
    }
    $unexpectedSkips = @($skipReasons | Where-Object { $allowedSkips -cnotcontains $_ })
    if ($skipped -ne $skipReasons.Count -or $unexpectedSkips.Count -ne 0) {
        throw "Unexpected conditional skips in '$Name': $($unexpectedSkips -join '; ')."
    }

    [ordered]@{
        name = $Name
        filter = if ($Filter) { $Filter } else { 'default' }
        realYmmpxLib = $WithLibrary
        totalGroups = $total
        passedGroups = $passed
        groupsWithSkips = $withSkips
        failedGroups = 0
        selectedNotRun = 0
        namedCasesPassed = [int]$caseCounts[1].Value
        namedCasesFailed = 0
        explicitSkips = $skipped
        conditionalSkipReasons = $skipReasons
        duration = $groupCounts[6].Value
    }
}

$results = @(
    Invoke-Regression 'none' $false ''
    Invoke-Regression 'real' $true ''
    Invoke-Regression 'https' $false 'GitHttpsAcceptanceTests'
    Invoke-Regression 'ymmpx' $true 'RealYmmpxR5Tests'
)
if ($results[0].totalGroups -ne $results[1].totalGroups) { throw 'The full regression runs selected different group counts.' }
Assert-Checkout
Assert-Hash $plugin $pluginHash
Assert-Hash $testedPlugin $pluginHash
Assert-Hash $runner $runnerHash
Assert-Hash $testAssembly $testAssemblyHash
Assert-Hash $ymmpxDll $ymmpxDllSha
Assert-Hash $metadataPath $metadataHash
foreach ($reference in $references.files) {
    Assert-Hash (Join-Path $referenceDirectory $reference.name) $reference.sha256
    Assert-Hash (Join-Path $runRoot $reference.name) $reference.sha256
}

$report = [ordered]@{
    schemaVersion = 1
    testedCommit = $ExpectedCommit
    tag = $Tag
    configuration = $Configuration
    sdk = $sdk
    testAppHostSha256 = $runnerHash
    testAssemblySha256 = $testAssemblyHash
    pluginSha256 = $pluginHash
    references = $references
    testLibrary = [ordered]@{
        release = 'v1.1.0'
        archiveSha256 = $ymmpxArchiveSha
        dllSha256 = $ymmpxDllSha
    }
    testSuite = 'Default suite with and without pinned YmmpxLib, plus isolated Git HTTPS and real YmmpxLib checks'
    regression = $results
    workflowRunId = $env:GITHUB_RUN_ID
    workflowRunAttempt = $env:GITHUB_RUN_ATTEMPT
}

if ($Configuration -eq 'Release') {
    $package = Join-Path $candidate 'YMMProjectManager.ymme'
    $files = [ordered]@{
        'LICENSE' = Join-Path $repo 'LICENSE'
        'readme.txt' = Join-Path $repo 'README.md'
        'YMMProjectManager.deps.json' = Join-Path $pluginBin 'YMMProjectManager.deps.json'
        'YMMProjectManager.dll' = $plugin
    }
    $pdb = Join-Path $pluginBin 'YMMProjectManager.pdb'
    if (Test-Path -LiteralPath $pdb -PathType Leaf) { $files['YMMProjectManager.pdb'] = $pdb }
    $stream = [IO.File]::Open($package, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entryName in $files.Keys) {
                $entry = $zip.CreateEntry($entryName)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $sourceStream = [IO.File]::OpenRead($files[$entryName])
                try {
                    $destination = $entry.Open()
                    try { $sourceStream.CopyTo($destination) }
                    finally { $destination.Dispose() }
                }
                finally {
                    $sourceStream.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    $readback = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        if ($readback.Entries.Count -ne $files.Count) { throw 'Unexpected package entries.' }
        foreach ($entryName in $files.Keys) {
            $entry = $readback.GetEntry($entryName)
            if (-not $entry) { throw "Missing package entry: $entryName" }
            $entryStream = $entry.Open()
            try {
                $packedHash = (Get-FileHash -InputStream $entryStream -Algorithm SHA256).Hash
                $expected = if ($entryName -eq 'YMMProjectManager.dll') { $pluginHash } else {
                    (Get-FileHash -LiteralPath $files[$entryName] -Algorithm SHA256).Hash
                }
                if ($packedHash -ne $expected) { throw "Packaged data mismatch: $entryName" }
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $readback.Dispose()
    }
    $report.package = 'YMMProjectManager.ymme'
    $report.packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $candidate 'YMMProjectManager.build.json') -Encoding utf8
