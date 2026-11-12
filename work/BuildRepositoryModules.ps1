param([Parameter(Mandatory)][string]$Compiler,[Parameter(Mandatory)][string]$Stage,[Parameter(Mandatory)][string]$CoreOutput)
$ErrorActionPreference='Stop'
$moduleStage=Join-Path $Stage 'repositories'
New-Item -ItemType Directory -Path $moduleStage -Force | Out-Null
$remoteLibraries=@((Get-ChildItem work\tools\sshnet -Filter '*.dll').FullName)
$shared=(Get-Item 'src\Backup.Repositories.Shared\AssemblyInfo.cs').FullName
foreach($type in @('Local','Smb','WebDav','Sftp')){
    $typeStage=Join-Path $moduleStage $type.ToLowerInvariant()
    New-Item -ItemType Directory -Path $typeStage -Force | Out-Null
    $sources=@((Get-ChildItem ('src\Backup.Repositories.'+$type) -Filter '*.cs').FullName)+@($shared)
    $symbols=@()
    if($type -eq 'Local' -or $type -eq 'Smb'){$sources+=@((Get-Item 'src\Backup.Repositories.FileSystem\FileSystemDestination.cs').FullName)}
    if($type -eq 'Local'){$symbols=@('/define:LOCAL_REPOSITORY')}
    $libraries=if($type -eq 'Sftp'){$remoteLibraries}else{@()}
    $inputs=Get-BuildInputHashes ($sources+$libraries+@($Compiler,$CoreOutput,$PSCommandPath))
    $output=Join-Path $typeStage ('Backup.Repository.'+$type+'.dll')
    & $Compiler /nologo /codepage:65001 /target:library /platform:x64 /optimize+ ('/out:'+$output) ('/r:'+$CoreOutput) /r:System.dll /r:System.Core.dll /r:System.Runtime.Serialization.dll @($libraries | ForEach-Object {'/r:'+$_}) @symbols @sources
    if($LASTEXITCODE -ne 0){throw ('Repository module build failed: '+$type)}
    Assert-BuildInputsUnchanged $inputs
}
Get-ChildItem work\tools\sshnet -File | Where-Object {$_.Extension -eq '.dll' -or $_.Name -eq 'THIRD-PARTY-SFTP.txt'} | Copy-Item -Destination (Join-Path $moduleStage 'sftp') -Force
