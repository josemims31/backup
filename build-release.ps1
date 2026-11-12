$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-ui.ps1') | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releases=Join-Path $PSScriptRoot 'releases'
if((Test-Path -LiteralPath $releases) -and ((Get-Item -LiteralPath $releases).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked release directory is not supported.'}
New-Item -ItemType Directory -Path $releases -Force | Out-Null
$archive=Join-Path $releases 'Backup-fang523-win-x64.zip'
if(Test-Path -LiteralPath $archive){if((Get-Item -LiteralPath $archive).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked release archive is not supported.'};Remove-Item -LiteralPath $archive -Force}
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $PSScriptRoot 'build'),$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
$archive
