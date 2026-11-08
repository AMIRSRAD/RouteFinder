param(
    [switch]$Check,
    [switch]$Classic,
    [string]$OutputDirectory = 'dist',
    [version]$ReleaseVersion,
    [uri]$ReleaseBaseUrl,
    [switch]$Installer,
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$SigningThumbprint,
    [string]$SignTool = 'signtool.exe',
    [string]$MakeNsis
)
$ErrorActionPreference = 'Stop'
if ($SigningThumbprint -and !$Installer) { throw 'Use -Installer to build a signed installer.' }
if ($ReleaseBaseUrl -and !$Classic -and !$Installer) { throw 'Use -Installer when publishing a WinUI update feed.' }
if ($SigningThumbprint) { $SignTool = (Get-Command $SignTool -CommandType Application).Source }
if ($Installer) {
    if (!$MakeNsis) {
        $MakeNsis = (Get-Command makensis.exe -CommandType Application -ErrorAction SilentlyContinue).Source
        if (!$MakeNsis) { $MakeNsis = Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe' }
    }
    $MakeNsis = (Get-Command $MakeNsis -CommandType Application).Source
}

function Sign-ReleaseFile([string]$FilePath) {
    & $SignTool sign /sha1 $SigningThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $FilePath." }
    & $SignTool verify /pa /tw $FilePath
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $FilePath." }
}

if ($ReleaseBaseUrl -and (!$ReleaseVersion -or !$ReleaseBaseUrl.IsAbsoluteUri -or
    $ReleaseBaseUrl.Scheme -ne 'https' -or $ReleaseBaseUrl.UserInfo -or
    $ReleaseBaseUrl.Query -or $ReleaseBaseUrl.Fragment -or !$ReleaseBaseUrl.AbsolutePath.EndsWith('/'))) {
    throw 'Use -ReleaseVersion with an HTTPS release directory URL ending in a slash.'
}
Push-Location $PSScriptRoot
try {
    if ($Check) {
        dotnet run --project tests/RouteFinder.Checks/RouteFinder.Checks.csproj -c Release --no-launch-profile
        if ($LASTEXITCODE -ne 0) { throw 'Backend checks failed.' }
        dotnet run --project tests/RouteFinder.ServiceChecks/RouteFinder.ServiceChecks.csproj -c Release --no-launch-profile
        if ($LASTEXITCODE -ne 0) { throw 'Service checks failed.' }
    }
    $outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    $publishPath = if ($Classic) { $outputPath } else { Join-Path $outputPath 'app' }
    $executable = Join-Path $publishPath 'RouteFinder.exe'
    $versionArgs = @()
    if ($ReleaseVersion) {
        $versionArgs = @("-p:Version=$ReleaseVersion", "-p:AssemblyVersion=$ReleaseVersion")
    }
    if ($Classic) {
        dotnet publish src/RouteFinder.App/RouteFinder.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outputPath @versionArgs
    } else {
        dotnet publish src/RouteFinder.WinUI/RouteFinder.WinUI.csproj -c Release -o $publishPath @versionArgs
    }
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination $outputPath
    if ($Check) {
        $checkArgs = if ($Classic) {
            '--self-test', ('"{0}"' -f (Join-Path $outputPath 'classic-self-test.json'))
        } else {
            '--capture-preview', ('"{0}"' -f (Join-Path $outputPath 'preview.png'))
        }
        $checkProcess = Start-Process -FilePath $executable -ArgumentList $checkArgs -WindowStyle Hidden -PassThru
        if (!$checkProcess.WaitForExit(90000)) {
            Stop-Process -InputObject $checkProcess
            throw 'Application checks timed out.'
        }
        if ($checkProcess.ExitCode -ne 0) { throw 'Application checks failed.' }
        if (!$Classic) {
            $startupReport = Join-Path $outputPath 'startup-check.txt'
            $startupProcess = Start-Process -FilePath $executable -ArgumentList '--check-startup',('"{0}"' -f $startupReport) -WindowStyle Hidden -PassThru
            if (!$startupProcess.WaitForExit(90000)) {
                Stop-Process -InputObject $startupProcess
                throw 'Startup check timed out.'
            }
            if (Test-Path -LiteralPath $startupReport) { Get-Content -LiteralPath $startupReport }
            if ($startupProcess.ExitCode -ne 0) { throw 'Startup check failed.' }
        }
    }
    if ($Installer) {
        $setupPath = Join-Path $outputPath 'RouteFinderSetup.exe'
        $pendingSetup = Join-Path $outputPath "RouteFinderSetup-$([guid]::NewGuid()).tmp"
        $appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
        $uninstallList = Join-Path $outputPath "uninstall-$([guid]::NewGuid()).nsh"
        try {
            if ($SigningThumbprint) { Sign-ReleaseFile $executable }
            $payloadArgs = @("/DAppPath=$executable")
            if (!$Classic) {
                $remove = @(
                    Get-ChildItem -LiteralPath $publishPath -File -Recurse |
                        Where-Object { $_.Extension -ne '.pdb' -and $_.FullName -ne $executable } |
                        ForEach-Object { 'Delete "$INSTDIR\app\{0}"' -f $_.FullName.Substring($publishPath.Length + 1) }
                    Get-ChildItem -LiteralPath $publishPath -Directory -Recurse |
                        Sort-Object { $_.FullName.Length } -Descending |
                        ForEach-Object { 'RMDir "$INSTDIR\app\{0}"' -f $_.FullName.Substring($publishPath.Length + 1) }
                )
                $remove | Set-Content -LiteralPath $uninstallList -Encoding utf8
                $payloadArgs += "/DAppDirectory=$publishPath", "/DUninstallList=$uninstallList"
            }
            & $MakeNsis /V2 @payloadArgs "/DSetupPath=$pendingSetup" "/DAppVersion=$appVersion" tools/RouteFinderSetup.nsi
            if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
            if ($SigningThumbprint) { Sign-ReleaseFile $pendingSetup }
            Move-Item -LiteralPath $pendingSetup -Destination $setupPath -Force
        } finally {
            if (Test-Path -LiteralPath $pendingSetup) { Remove-Item -LiteralPath $pendingSetup }
            if (Test-Path -LiteralPath $uninstallList) { Remove-Item -LiteralPath $uninstallList }
        }
    }
    if ($ReleaseBaseUrl) {
        $package = if ($Installer) { $setupPath } else { $executable }
        [ordered]@{
            product = 'RouteFinder'
            version = $ReleaseVersion.ToString()
            download_url = [uri]::new($ReleaseBaseUrl, (Split-Path -Leaf $package)).AbsoluteUri
            release_page = if ($ReleaseBaseUrl.Host -eq 'github.com' -and $ReleaseBaseUrl.AbsolutePath -match '^/(.+)/releases/download/([^/]+)/$') {
                "https://github.com/$($Matches[1])/releases/tag/$($Matches[2])"
            }
            sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'manifest.json') -Encoding utf8
    }
} finally { Pop-Location }
