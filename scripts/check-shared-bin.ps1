# Fails when the programs published into one bin folder need different versions of the same DLL.
# (The last publish overwrites the file; the other program then crashes on start with
#  "Could not load file or assembly ... Version=X".)
# Usage: pwsh scripts/check-shared-bin.ps1 <bin folder>
param([Parameter(Mandatory = $true)][string]$Bin)

$ErrorActionPreference = 'Stop'
$seen = @{}
foreach ($depsFile in Get-ChildItem -Path $Bin -Filter '*.deps.json') {
    $deps = Get-Content -Raw -Path $depsFile.FullName | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($lib in $target.Value.PSObject.Properties) {
            $runtime = $lib.Value.runtime
            if (-not $runtime) { continue }
            foreach ($dll in $runtime.PSObject.Properties) {
                $name = Split-Path -Leaf $dll.Name
                $version = $dll.Value.assemblyVersion
                if (-not $version) { continue }
                if (-not $seen.ContainsKey($name)) { $seen[$name] = @{} }
                $seen[$name][$depsFile.Name] = "$version ($($lib.Name))"
            }
        }
    }
}

$conflicts = @()
foreach ($name in $seen.Keys) {
    $versions = $seen[$name].Values | ForEach-Object { ($_ -split ' ')[0] } | Sort-Object -Unique
    if (@($versions).Count -gt 1) {
        $detail = ($seen[$name].GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
        $conflicts += "$name -> $detail"
    }
}

if ($conflicts.Count) {
    Write-Host "Shared bin folder has DLL version conflicts (pin the same version in every project):"
    $conflicts | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "Shared bin folder: $($seen.Count) assemblies, no version conflicts."
