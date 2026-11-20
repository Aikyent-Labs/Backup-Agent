<#
  CloudOrc Backup Agent - one-line installer for Windows (served by the CloudOrc portal). Elevated PowerShell:

    & ([scriptblock]::Create((irm https://<portal>/install/backup.ps1))) -Code ABCD-1234 `
        -DownloadBase https://<downloads>/backup-agent/1.0.0 -ApiBase https://<api>/

  Downloads CloudOrcBackupSetup.exe, verifies its SHA-256 (and Authenticode signature when signed),
  then installs silently. Nothing runs if the checksum does not match.
#>
param(
    [string]$Code = "",
    [Parameter(Mandatory = $true)][string]$DownloadBase,
    [string]$ApiBase = "",
    [string]$PortalUrl = "",
    [switch]$Server   # no status icon
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated (Administrator) PowerShell."
}
if (-not $DownloadBase.StartsWith("https://")) { throw "-DownloadBase must be an https:// URL." }

$work = Join-Path $env:TEMP ("cloudorc-backup-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    $setup = Join-Path $work "CloudOrcBackupSetup.exe"
    Invoke-WebRequest "$DownloadBase/CloudOrcBackupSetup.exe" -OutFile $setup -UseBasicParsing
    # Release hosts serve .sha256 as application/octet-stream: Windows PowerShell 5.1 then returns
    # Content as Byte[], so decode it explicitly instead of splitting the bytes.
    $shaContent = (Invoke-WebRequest "$DownloadBase/CloudOrcBackupSetup.exe.sha256" -UseBasicParsing).Content
    if ($shaContent -is [byte[]]) { $shaContent = [Text.Encoding]::ASCII.GetString($shaContent) }
    $expected = ([string]$shaContent).Trim().Split(@(' ', "`t", "`r", "`n"), [StringSplitOptions]::RemoveEmptyEntries)[0].ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$') { throw "Could not read the published SHA-256 checksum. Nothing was installed." }
    $actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Checksum mismatch - the download is corrupt or was tampered with. Nothing was installed." }

    $sig = Get-AuthenticodeSignature $setup
    if ($sig.Status -ne "Valid" -and $sig.Status -ne "NotSigned") { throw "Installer signature is $($sig.Status). Nothing was installed." }

    $setupArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$work\setup.log")
    if ($Code) { $setupArgs += "/CODE=$Code" }
    if ($ApiBase) { $setupArgs += "/API-BASE=$ApiBase" }
    if ($PortalUrl) { $setupArgs += "/PORTAL-URL=$PortalUrl" }
    if ($Server) { $setupArgs += '/COMPONENTS=agent' }

    Write-Host "Installing CloudOrc Backup Agent..."
    $p = Start-Process -FilePath $setup -ArgumentList $setupArgs -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Setup failed with exit code $($p.ExitCode). Log: $work\setup.log" }
    Write-Host "CloudOrc Backup Agent installed. Status: & 'C:\Program Files\CloudOrc\Backup\bin\cloudorc-backup.exe' status"
}
finally {
    if ($null -eq $Error[0]) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
