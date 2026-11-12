$script:BackupProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Invoke-WithProjectCache {
    param([Parameter(Mandatory)][scriptblock]$Body)
    $cache = Join-Path $script:BackupProjectRoot 'cache'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $env:TEMP = $cache
        $env:TMP = $cache
        & $Body
    } finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }
}
