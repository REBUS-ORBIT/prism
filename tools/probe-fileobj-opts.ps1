Add-Type -Path 'C:\Program Files\Rhino 8\System\RhinoCommon.dll'

Write-Host '=== FileObjReadOptions property read/write ==='
[Rhino.FileIO.FileObjReadOptions].GetProperties() | Sort-Object Name | ForEach-Object {
    '{0,-32} get={1} set={2} type={3}' -f $_.Name, $_.CanRead, $_.CanWrite, $_.PropertyType.FullName
}

Write-Host ''
Write-Host '=== FileReadOptions property read/write ==='
[Rhino.FileIO.FileReadOptions].GetProperties() | Sort-Object Name | ForEach-Object {
    '{0,-40} get={1} set={2} type={3}' -f $_.Name, $_.CanRead, $_.CanWrite, $_.PropertyType.FullName
}

Write-Host ''
Write-Host '=== Default-constructed FileObjReadOptions values ==='
$readOpts = New-Object Rhino.FileIO.FileReadOptions
$readOpts.ImportMode = $true
$readOpts.BatchMode  = $true
$objOpts = New-Object Rhino.FileIO.FileObjReadOptions $readOpts
[Rhino.FileIO.FileObjReadOptions].GetProperties() | Sort-Object Name | ForEach-Object {
    $v = $_.GetValue($objOpts, $null)
    "{0,-32} = {1}" -f $_.Name, $v
}
