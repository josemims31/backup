param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
if(-not (Test-Path -LiteralPath (Join-Path $root 'Backup.exe'))){throw 'Build output is missing.'}
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.Name -ne 'installation-manifest.json'} | Sort-Object FullName | ForEach-Object {
    if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked build files are not supported.'}
    [ordered]@{Path=$_.FullName.Substring($root.Length+1).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'Backup.exe')).FileVersion
$manifest=[ordered]@{Format=1;Product='Backup-fang523';Version=$version;Files=$files}
[IO.File]::WriteAllText((Join-Path $root 'installation-manifest.json'),($manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
