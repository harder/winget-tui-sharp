<#
.SYNOPSIS
  Build (and optionally sign / test) the download-B MSIX: a Native-AOT, package-identity build of
  winget-tui-sharp that reaches the in-box out-of-process WinGet COM server. See ../com-activation.md.

.DESCRIPTION
  Steps: publish the WingetComMode=Identity AOT build -> stage the package layout (exe + 61 KB winmd
  + manifest + logos, minus the .pdb) -> MakeAppx pack -> optionally signtool sign.

  On an ARM64 host the AOT publish must run inside a VS Dev Shell (ILC calls a bare vswhere.exe) —
  see README / com-activation.md. GitHub's windows-latest x64 runner needs no special shell.

.EXAMPLE
  # Local self-signed build + prove COM activates under the package identity (Developer Mode):
  pwsh ./packaging/build-msix.ps1 -Arch arm64 -SelfSigned -TestRegister

.EXAMPLE
  # CI: pack only (sign separately with Azure Trusted Signing, see code-signing.md):
  pwsh ./packaging/build-msix.ps1 -Arch x64 -Version 0.1.2.0
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'arm64')] [string]$Arch = 'arm64',
  [string]$Version = '0.1.2.0',
  [string]$Publisher = 'CN=winget-tui-sharp (Dev)',
  [string]$CertPath,
  [string]$CertPassword = 'spike',
  [switch]$SelfSigned,
  [switch]$SkipPublish,
  [switch]$TestRegister,
  [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must be 4-part (e.g. 0.1.2.0); got '$Version'." }
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$stage = Join-Path ([IO.Path]::GetTempPath()) "wts-msix-$Arch"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# --- 1. Publish the Identity AOT build -------------------------------------------------------------
if (-not $SkipPublish) {
  Write-Host "==> Publishing WingetComMode=Identity AOT ($Arch)..."
  dotnet publish (Join-Path $repo 'WingetTuiSharp.csproj') `
    -c Release -f net10.0-windows10.0.26100.0 -r "win-$Arch" `
    -p:WingetComMode=Identity -p:Version=$Version -o $stage
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed (AOT publish on ARM64 needs a VS Dev Shell).' }
}
Remove-Item (Join-Path $stage '*.pdb') -Force -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $stage 'winget-tui-sharp.exe'))) { throw "exe missing from stage; pass without -SkipPublish." }
if (-not (Test-Path (Join-Path $stage 'Microsoft.Management.Deployment.winmd'))) {
  throw "winmd missing from stage — the Identity build must copy it (needed for AOT activation)."
}

# --- 2. Logos (generated if not committed) --------------------------------------------------------
$assets = Join-Path $stage 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
function New-Logo([string]$path, [int]$w, [int]$h) {
  $committed = Join-Path $PSScriptRoot ("assets/" + (Split-Path $path -Leaf))
  if (Test-Path $committed) { Copy-Item $committed $path -Force; return }
  try {
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(26, 20, 16))            # theme background
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 130, 30)) # amber
    $m = [int]($w * 0.18)
    $g.FillRectangle($brush, $m, $m, $w - 2 * $m, $h - 2 * $m)
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  }
  catch {
    # Fallback 1x1 PNG — MakeAppx accepts it; replace with real art before a public release.
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII='))
  }
}
New-Logo (Join-Path $assets 'StoreLogo.png') 50 50
New-Logo (Join-Path $assets 'Square150x150Logo.png') 150 150
New-Logo (Join-Path $assets 'Square44x44Logo.png') 44 44

# --- 3. Manifest (placeholder substitution) -------------------------------------------------------
$mani = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$mani = $mani.Replace('{VERSION}', $Version).Replace('{ARCH}', $Arch).Replace('{PUBLISHER}', $Publisher)
Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $mani -Encoding utf8

# --- 4. Pack -------------------------------------------------------------------------------------
function Find-SdkTool([string]$name) {
  $cmd = Get-Command $name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($root in @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")) {
    if (Test-Path $root) {
      $hit = Get-ChildItem $root -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(x64|arm64)\\' } | Sort-Object FullName -Descending | Select-Object -First 1
      if ($hit) { return $hit.FullName }
    }
  }
  throw "$name not found — install the Windows 10/11 SDK."
}
$msix = Join-Path $OutDir "winget-tui-sharp-$Version-$Arch.msix"
$makeappx = Find-SdkTool 'makeappx.exe'
Write-Host "==> Packing $msix"
& $makeappx pack /o /d $stage /p $msix
if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }

# --- 5. Sign -------------------------------------------------------------------------------------
if ($SelfSigned -and -not $CertPath) {
  Write-Host "==> Creating a self-signed cert ($Publisher)..."
  $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
    -FriendlyName 'winget-tui-sharp dev signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
  $CertPath = Join-Path $OutDir 'wts-dev.pfx'
  Export-PfxCertificate -Cert $cert -FilePath $CertPath `
    -Password (ConvertTo-SecureString $CertPassword -Force -AsPlainText) | Out-Null
  Write-Host "    pfx: $CertPath  (public users need this cert trusted; dev installs need Trusted People + admin)"
}
if ($CertPath) {
  $signtool = Find-SdkTool 'signtool.exe'
  Write-Host "==> Signing $msix"
  & $signtool sign /fd SHA256 /a /f $CertPath /p $CertPassword $msix
  if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed (Publisher must match the cert subject).' }
}

Write-Host "DONE: $msix"

# --- 6. Optional: prove COM activates under the package identity (Developer Mode, no admin) -------
if ($TestRegister) {
  Write-Host "==> Test: loose-registering the layout and running --comdiag with identity..."
  Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml')
  $pkg = Get-AppxPackage -Name winget-tui-sharp | Select-Object -First 1
  $out = Join-Path $OutDir 'comdiag-identity.txt'
  Remove-Item $out -ErrorAction SilentlyContinue
  $exe = Join-Path $stage 'winget-tui-sharp.exe'
  $inner = "/c `"`"$exe`" --comdiag > `"$out`" 2>&1`""
  Invoke-CommandInDesktopPackage -PackageFamilyName $pkg.PackageFamilyName -AppId 'wingettuisharp' `
    -Command "$env:ComSpec" -Args $inner -PreventBreakaway
  Start-Sleep -Seconds 5
  if (Test-Path $out) { Write-Host "--- comdiag (with identity) ---"; Get-Content $out }
  else { Write-Host "(no comdiag output captured)" }
  Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
  Write-Host "    (test package unregistered)"
}
