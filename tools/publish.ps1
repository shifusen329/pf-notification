<#
.SYNOPSIS
    Builds PfNotification and publishes it to the private Dalamud plugin repository on the reverse proxy.

.DESCRIPTION
    1. Refuses to run if the plugin sources have uncommitted changes, so every release matches a commit.
    2. Builds Release and runs the unit tests.
    3. Writes repo.json (a one-entry Dalamud custom repository) from the manifest DalamudPackager generated,
       plus source.zip (git archive of the plugin sources: AGPL-3.0 requires offering source to recipients).
    4. Copies repo.json, latest.zip and source.zip to /var/www/pfnotification/<RepoPath>/ on the proxy.

    FC mates add <BaseUrl>/<RepoPath>/repo.json under /xlsettings > Experimental > Custom Plugin Repositories.
    Dalamud only offers an update when AssemblyVersion increases, so bump <Version> in
    PfNotification/PfNotification.csproj (and commit) before each release.

.PARAMETER DryRun
    Build and write the files to the Release output folder, but don't upload anything.

.EXAMPLE
    pwsh tools/publish.ps1 -DryRun
    pwsh tools/publish.ps1
#>
param(
    [string] $ProxyHost = 'administrator@192.168.0.100',
    [string] $BaseUrl = 'https://plugins.shifusenproductions.com',
    [ValidatePattern('^[0-9a-f]{16}$')]
    [string] $RepoPath = 'da52407735eff741',
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sourcePaths = @('PfNotification', 'tests', 'PfNotification.slnx', 'LICENSE.md', 'README.md', '.editorconfig')

Push-Location $root
try
{
    $dirty = git status --porcelain -- @sourcePaths
    if ($dirty -and -not $DryRun)
    {
        throw "Uncommitted changes in the plugin sources; commit them first:`n$($dirty -join "`n")"
    }
    elseif ($dirty)
    {
        Write-Warning 'Uncommitted changes: the build uses the working tree, but source.zip is taken from HEAD.'
    }

    dotnet build PfNotification.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/PfNotification.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    $out = Join-Path $root 'PfNotification/bin/x64/Release/PfNotification'
    $manifest = Get-Content (Join-Path $out 'PfNotification.json') -Raw | ConvertFrom-Json
    $base = "$($BaseUrl.TrimEnd('/'))/$RepoPath"

    # A repo entry is the generated manifest plus the repo-only fields.
    $entry = [ordered]@{}
    foreach ($property in $manifest.PSObject.Properties)
    {
        $entry[$property.Name] = $property.Value
    }
    $entry['IsHide'] = $false
    $entry['IsTestingExclusive'] = $false
    $entry['DownloadLinkInstall'] = "$base/latest.zip"
    $entry['DownloadLinkUpdate'] = "$base/latest.zip"
    $entry['DownloadLinkTesting'] = "$base/latest.zip"
    $entry['LastUpdate'] = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

    $repoJson = Join-Path $out 'repo.json'
    ConvertTo-Json -InputObject @($entry) -Depth 5 | Set-Content -Path $repoJson -Encoding utf8NoBOM

    Write-Host "PfNotification $($manifest.AssemblyVersion) packaged in $out"
    if ($DryRun)
    {
        Write-Host "Dry run: wrote $repoJson; source.zip is only built (from HEAD) when publishing. Not uploading."
        Write-Host "Repo URL: $base/repo.json"
        return
    }

    $sourceZip = Join-Path $out 'source.zip'
    git archive --format=zip --output=$sourceZip HEAD -- @sourcePaths
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }

    # Stage in a fresh private (0700) folder. A fixed /tmp path could be pre-created by another account on the proxy,
    # which could then swap the files before root installs them into the public web root.
    $staging = (ssh $ProxyHost 'mktemp -d /tmp/pfnotification.XXXXXXXXXX' | Out-String).Trim()
    if (($LASTEXITCODE -ne 0) -or ($staging -notmatch '^/tmp/pfnotification\.[A-Za-z0-9]{10}$'))
    {
        throw "Could not create a staging folder on the proxy (got '$staging')."
    }

    scp (Join-Path $out 'latest.zip') $repoJson $sourceZip "${ProxyHost}:$staging/"
    if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }
    ssh $ProxyHost "sudo -n install -d -m 755 /var/www/pfnotification/$RepoPath && sudo -n install -m 644 $staging/latest.zip $staging/repo.json $staging/source.zip /var/www/pfnotification/$RepoPath/ && rm -rf $staging"
    if ($LASTEXITCODE -ne 0) { throw 'Install on the proxy failed.' }

    $published = Invoke-RestMethod "$base/repo.json"
    Write-Host "Published PfNotification $($published[0].AssemblyVersion): $base/repo.json"
}
finally
{
    Pop-Location
}
