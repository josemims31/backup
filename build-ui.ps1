$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'work\ProjectPaths.ps1')
. (Join-Path $PSScriptRoot 'work\BuildInputs.ps1')
Invoke-WithProjectCache {
    Set-Location -LiteralPath $PSScriptRoot
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $wpf=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
    $build=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build')).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($build) -ne $script:BackupProjectRoot -or [IO.Path]::GetFileName($build) -ne 'build'){throw 'Build directory escaped the project.'}
    foreach($path in @($PSScriptRoot,$build)){if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked build directory is not supported.'}}
    if(Test-Path -LiteralPath $build){
        if(@(Get-ChildItem -LiteralPath $build -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Linked build content is not supported.'}
        Remove-Item -LiteralPath $build -Recurse -Force
    }
    $stage=$build
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    & (Join-Path $PSScriptRoot 'work\build-app-icon.ps1')
    $coreSources=@((Get-ChildItem src\Backup.Core -Filter '*.cs').FullName)
    $coreInputs=Get-BuildInputHashes ($coreSources+@($compiler,$PSCommandPath))
    $coreOutput=Join-Path $stage 'Backup.Core.dll'
    & $compiler /nologo /codepage:65001 /target:library /platform:x64 /optimize+ "/out:$coreOutput" /r:System.Core.dll /r:System.Runtime.Serialization.dll @coreSources
    if($LASTEXITCODE -ne 0){throw 'Core build failed.'}
    Assert-BuildInputsUnchanged $coreInputs
    & (Join-Path $PSScriptRoot 'work\BuildRepositoryModules.ps1') -Compiler $compiler -Stage $stage -CoreOutput $coreOutput
    $updateSources=@((Get-ChildItem src\Backup.Updates -Filter '*.cs').FullName)
    $updaterSources=@((Get-ChildItem src\Backup.Updater -Filter '*.cs').FullName)+$updateSources
    $updaterStage=Join-Path $stage 'updater'
    New-Item -ItemType Directory -Path $updaterStage -Force | Out-Null
    $updaterInputs=Get-BuildInputHashes ($updaterSources+@($compiler,$PSCommandPath,'src\Backup.Desktop\app.manifest'))
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /win32manifest:src\Backup.Desktop\app.manifest ("/out:"+(Join-Path $updaterStage 'Backup.Updater.exe')) /r:System.Core.dll /r:System.Runtime.Serialization.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll @updaterSources
    if($LASTEXITCODE -ne 0){throw 'Updater build failed.'}
    Assert-BuildInputsUnchanged $updaterInputs
    Copy-Item -LiteralPath src\Backup.Updater\Backup.Updater.exe.config -Destination $updaterStage -Force
    $uiSources=@((Get-ChildItem src\Backup.Desktop -Filter '*.cs').FullName)+$updateSources
    $appIcon=Join-Path $PSScriptRoot 'cache\backup-icon.ico'
    $browserIcons=@((Get-ChildItem assets\browser -Filter '*.png').FullName)
    $iconResources=@($browserIcons | ForEach-Object {'/resource:'+$_+',Backup.Desktop.BrowserIcons.'+[IO.Path]::GetFileName($_)})
    $uiInputs=Get-BuildInputHashes ($uiSources+$browserIcons+@($compiler,$PSCommandPath,$coreOutput,$appIcon,'src\Backup.Desktop\MainWindow.xaml','src\Backup.Desktop\app.manifest'))
    $uiOutput=Join-Path $stage 'Backup.exe'
    $references=@("/r:$coreOutput",'/r:System.dll','/r:Microsoft.CSharp.dll','/r:System.Security.dll','/r:System.Core.dll','/r:System.Runtime.Serialization.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Xaml.dll',"/r:$wpf\WindowsBase.dll",'/r:System.Windows.Forms.dll',"/r:$wpf\PresentationFramework.dll","/r:$wpf\PresentationCore.dll","/r:$wpf\UIAutomationClient.dll","/r:$wpf\UIAutomationTypes.dll")
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /win32manifest:src\Backup.Desktop\app.manifest "/win32icon:$appIcon" "/out:$uiOutput" '/resource:src\Backup.Desktop\MainWindow.xaml,Backup.Desktop.MainWindow.xaml' ("/resource:"+$appIcon+",Backup.Desktop.AppIcon.ico") @iconResources @references @uiSources
    if($LASTEXITCODE -ne 0){throw 'UI build failed.'}
    Assert-BuildInputsUnchanged $uiInputs
    Copy-Item -LiteralPath src\Backup.Desktop\Backup.exe.config -Destination (Join-Path $stage 'Backup.exe.config') -Force
    Copy-Item work\tools\everything-sdk\dll\Everything64.dll $stage -Force
    Copy-Item work\tools\7zip\x64\7za.dll $stage -Force
    Copy-Item work\tools\7zip\License.txt (Join-Path $stage 'LICENSE-7ZIP.txt') -Force
    Copy-Item work\tools\everything-sdk\LICENSE.txt (Join-Path $stage 'LICENSE-EVERYTHING.txt') -Force
    foreach($notice in @('LICENSE','NOTICE','THIRD_PARTY_NOTICES.md')){
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $notice) -Destination (Join-Path $stage $notice) -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse -Force
    & (Join-Path $PSScriptRoot 'work\BuildUpdateManifest.ps1') -BuildDirectory $stage
    $stage
}
