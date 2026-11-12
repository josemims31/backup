$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$source=Join-Path $project 'assets\app-icon.png'
$cache=Join-Path $project 'cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $cache 'Backup.IconWriter.exe'
& $compiler /nologo /platform:x64 /target:exe /r:System.Drawing.dll "/out:$exe" (Join-Path $PSScriptRoot 'IconWriter.cs')
if($LASTEXITCODE -ne 0){throw 'Icon converter build failed.'}
$target=Join-Path $cache 'backup-icon.ico'
& $exe $source $target
if($LASTEXITCODE -ne 0){throw 'Icon conversion failed.'}
