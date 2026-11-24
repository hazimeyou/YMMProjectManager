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
    $changed = git -C $repo status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $changed) {
        throw 'The tracked checkout must remain unchanged throughout verification.'
    }
}

$metadataPath = Join-Path $referenceDirectory 'YMM4-reference.json'
$metadataHash = (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash
$references = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$requiredReferences = 'YukkuriMovieMaker.Plugin.dll', 'YukkuriMovieMaker.dll', 'YukkuriMovieMaker.Controls.dll'
if ($references.files.Count -ne $requiredReferences.Count) { throw 'Unexpected YMM4 reference count.' }
foreach ($name in $requiredReferences) {
    $reference = @($references.files | Where-Object name -CEQ $name)
    if ($reference.Count -ne 1) { throw "Missing or duplicate YMM4 reference: $name" }
    Assert-Hash (Join-Path $referenceDirectory $name) $reference[0].sha256
}

$ymmpxArchiveSha = '9D5EE82C96D6A5ED57D805306CFE037A6669F0D3D15D6C28919E1A59158680DA'
$ymmpxDllSha = '107A522AAC5C0AAFD32494C0C93AC9404694FBA6A4BAD7902B78D0F000C2E552'
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
$env:YMM4_PATH = $referenceDirectory
$env:YMM4DirPath = $referenceDirectory
Remove-Item Env:YMM4DeployPath -ErrorAction SilentlyContinue
Assert-Checkout
Push-Location -LiteralPath $output
try {
    $sdk = dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -ne $expectedSdk) { throw 'The pinned SDK is not active.' }
    dotnet build (Join-Path $repo 'YMMProjectManager.sln') -c $Configuration --artifacts-path $artifacts "-p:Version=$($Tag.Substring(1))" -p:ContinuousIntegrationBuild=true
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
        YMMPX_LIB_PATH = if ($WithLibrary) { $ymmpxDll } else { $null }
        YMM_TEST_FILTER = $Filter
        YMM_TEST_YMMPX_RELEASED_FIXTURES = $null
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
        explicitSkips = $skipped
        conditionalSkipReasons = @($lines -match '^SKIP conditional case: ' | ForEach-Object { $_.Substring(23) })
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
