function Get-BuildInputHashes {
    param([string[]]$Paths)
    @($Paths | Sort-Object -Unique | ForEach-Object {
        $entry=Get-Item -LiteralPath $_ -ErrorAction Stop
        [pscustomobject]@{Path=$entry.FullName;Hash=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash}
    })
}
function Assert-BuildInputsUnchanged {
    param([object[]]$Inputs)
    foreach($inputFile in $Inputs){if((Get-FileHash -LiteralPath $inputFile.Path -Algorithm SHA256).Hash -ne $inputFile.Hash){throw "Input changed during build/validation: $($inputFile.Path)"}}
}
