[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$sdkVersion = & $dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') {
    throw 'Install the .NET 10 SDK before running Build.ps1.'
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("zapret-app-pff-" + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'windows'
$payload = Join-Path $stage 'windows.zip'
$launcher = Join-Path $stage 'installer'
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    $app = Join-Path $PSScriptRoot 'src/Zapret_app_Pff/Zapret_app_Pff.csproj'
    & $dotnet publish $app -c Release -r win-x64 --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }

    $notices = Join-Path $publish 'licenses'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'LICENSE') (Join-Path $notices 'Zapret_app_Pff.txt')
    $line = & $dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'NuGet did not report its package cache.' }
    $packages = ($line -split ': ', 2)[1].Trim()
    $about = Get-Content (Join-Path $publish 'Zapret_app_Pff.deps.json') -Raw | ConvertFrom-Json
    $libraries = $about.libraries.PSObject.Properties
    foreach ($library in $libraries) {
        $name, $version = $library.Name -split '/', 2
        $name = $name -replace '^runtimepack\.', ''
        $path = Join-Path $packages ("{0}/{1}" -f $name.ToLowerInvariant(), $version)
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $path -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD[- ]PARTY[- ]NOTICES)(\.txt|\.md)?$' } |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $notices ("{0}-{1}" -f $name, $_.Name)) }
    }

    $uninstaller = Join-Path $PSScriptRoot 'src/Uninstaller/Uninstaller.csproj'
    $maintenance = Join-Path $stage 'uninstaller'
    & $dotnet build $uninstaller -c Release -o $maintenance
    if ($LASTEXITCODE -ne 0) { throw 'Uninstaller build failed.' }
    Copy-Item (Join-Path $maintenance 'Uninstall.exe') $publish

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

    $project = Join-Path $PSScriptRoot 'src/Installer/Installer.csproj'
    & $dotnet build $project -c Release -o $launcher "-p:InstallerPayload=$payload"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $output = Join-Path $OutputDirectory 'Zapret_app_Pff.exe'
    Copy-Item (Join-Path $launcher 'Zapret_app_Pff.exe') $output -Force
    Write-Host ([IO.Path]::GetFullPath($output))
    Get-FileHash -Algorithm SHA256 -LiteralPath $output
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
