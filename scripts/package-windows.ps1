<#
.SYNOPSIS
  Builds the Windows install tree, the status icon and (if Inno Setup is installed) CloudOrcBackupSetup.exe.
.EXAMPLE
  .\scripts\package-windows.ps1 -Version 1.0.0 -DefaultApiBase https://api.cloudorc.example/
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$DefaultApiBase = "",
    [string]$DefaultPortalUrl = "",
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$rid = "win-x64"
$out = Join-Path $root "dist\$rid\cloudorc-backup"
$bin = Join-Path $out "bin"
$engines = Join-Path $out "engines"
$tray = Join-Path $root "dist\$rid\tray"

Remove-Item -Recurse -Force (Join-Path $root "dist\$rid") -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $bin, $engines, $tray | Out-Null

# All four programs share ONE self-contained runtime in bin\ (small download, no .NET install needed).
foreach ($project in "CloudOrc.Backup.Agent", "CloudOrc.Backup.Worker", "CloudOrc.Backup.Watchdog", "CloudOrc.Backup.Updater") {
    Write-Host "--> publish $project"
    dotnet publish "$root\src\$project\$project.csproj" -c Release -r $rid --self-contained true `
        -p:Version=$Version -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -o $bin -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish $project failed" }
}

Write-Host "--> publish CloudOrc.Backup.Tray"
dotnet publish "$root\src\CloudOrc.Backup.Tray\CloudOrc.Backup.Tray.csproj" -c Release -r $rid --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=none -o $tray -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish tray failed" }

# ---- kopia, SHA-256 pinned (archive hash from engines.lock.json, binary hash into the manifest)
$lock = Get-Content (Join-Path $root "engines\engines.lock.json") -Raw | ConvertFrom-Json
$engine = $lock.engines | Where-Object { $_.rid -eq $rid } | Select-Object -First 1
$cache = Join-Path $root "engines\.cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$archive = Join-Path $cache (Split-Path $engine.url -Leaf)
if (-not (Test-Path $archive)) { Invoke-WebRequest -Uri $engine.url -OutFile $archive -UseBasicParsing }
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $engine.sha256) { Remove-Item $archive; throw "SHA-256 mismatch for $($engine.url): $actual" }
$extract = Join-Path ([IO.Path]::GetTempPath()) ("kopia-" + [guid]::NewGuid())
Expand-Archive $archive -DestinationPath $extract
Copy-Item (Get-ChildItem $extract -Recurse -Filter $engine.exe | Select-Object -First 1).FullName (Join-Path $engines $engine.exe)
Remove-Item -Recurse -Force $extract
$binSha = (Get-FileHash (Join-Path $engines $engine.exe) -Algorithm SHA256).Hash.ToLowerInvariant()
@{ engines = @(@{ name = $engine.name; version = $engine.version; file = $engine.exe; sha256 = $binSha }) } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $engines "engines.manifest.json") -Encoding utf8
Set-Content (Join-Path $out "VERSION") $Version -Encoding ascii
Write-Host "--> kopia $($engine.version) verified ($binSha)"

if (Test-Path $Iscc) {
    $numeric = ($Version -split '-')[0]
    & $Iscc "/DMyAppVersion=$Version" "/DNumericVersion=$numeric" "/DSourceDir=$out" "/DTraySourceDir=$tray" `
        "/DDefaultApiBase=$DefaultApiBase" "/DDefaultPortalUrl=$DefaultPortalUrl" `
        "/O$root\dist" "$root\installer\windows\CloudOrcBackupSetup.iss"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
    $setup = Join-Path $root "dist\CloudOrcBackupSetup.exe"
    "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant())  CloudOrcBackupSetup.exe" |
        Set-Content "$setup.sha256" -Encoding ascii
    Write-Host "==> $setup"
} else {
    Write-Warning "Inno Setup not found at $Iscc - install tree is ready in $out, setup.exe was not built."
}
