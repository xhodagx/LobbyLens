<#
  LobbyLens release runbook. Builds, packages, SIGNS, and publishes a release so
  every installed copy self-updates on its next HDT restart.

  The private signing key lives OUTSIDE this repo and must never be committed or
  uploaded anywhere. A package that isn't signed by it will be refused by every
  shipped binary (Updater.cs pins the public key), so the blob is a dumb pipe.

  Usage:  ./release.ps1                      # release whatever version the csproj says
          ./release.ps1 -DryRun              # build/zip/sign only; no uploads
#>
param(
  [string] $KeyPath = "$env:USERPROFILE\.lobbylens\signing\lobbylens-release-key1.xml",
  [string] $StorageAccount = 'stdatayififhlgyqepq',
  [string] $SubscriptionId = '<subscription-id>',
  [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

# --- version from the csproj (single source of truth) -----------------------
$csproj = Join-Path $repo 'LobbyLens\LobbyLens.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'no <Version> in csproj' }
Write-Host "==> Releasing v$version"

# --- build + package ---------------------------------------------------------
dotnet build $csproj -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$dll = Join-Path $repo 'LobbyLens\bin\Release\LobbyLens.dll'
$zip = Join-Path $repo "LobbyLens-v$version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $dll -DestinationPath $zip
Write-Host "==> Packaged $zip ($([math]::Round((Get-Item $zip).Length/1KB)) KB)"

# --- sign --------------------------------------------------------------------
if (-not (Test-Path $KeyPath)) { throw "signing key not found: $KeyPath" }
$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.FromXmlString((Get-Content $KeyPath -Raw))
$bytes = [System.IO.File]::ReadAllBytes($zip)
$sig = [Convert]::ToBase64String($rsa.SignData($bytes,
  [System.Security.Cryptography.HashAlgorithmName]::SHA256,
  [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host "==> Signed (sha256 $($sha.Substring(0,16))…)"

if ($DryRun) { Write-Host '==> DryRun: skipping uploads'; return }

# --- publish package + updated meta.json -------------------------------------
$key = az storage account keys list -n $StorageAccount --subscription $SubscriptionId --query '[0].value' -o tsv
$pkgBlob = "releases/LobbyLens-v$version.zip"
az storage blob upload --account-name $StorageAccount --account-key $key -c public -n $pkgBlob -f $zip `
  --content-type 'application/zip' --overwrite --output none

# merge into the existing meta.json so support links etc. are preserved.
# HARD-FAIL if unreachable: publishing from an empty object would wipe the
# kofi/lightning/btc links every shipped binary reads.
$metaUrl = "https://$StorageAccount.blob.core.windows.net/public/meta.json"
try { $meta = Invoke-RestMethod $metaUrl }
catch { throw "meta.json unreachable at $metaUrl — refusing to publish (would wipe support links): $($_.Exception.Message)" }
$set = @{
  latest = "$version"
  url    = 'https://github.com/xhodagx/LobbyLens/releases'
  pkg    = "https://$StorageAccount.blob.core.windows.net/public/$pkgBlob"
  sig    = $sig
  sha256 = $sha
}
foreach ($k in $set.Keys) {
  if ($meta.PSObject.Properties[$k]) { $meta.$k = $set[$k] }
  else { $meta | Add-Member -NotePropertyName $k -NotePropertyValue $set[$k] }
}
$metaFile = Join-Path $env:TEMP 'lobbylens-meta.json'
$meta | ConvertTo-Json | Out-File $metaFile -Encoding utf8
az storage blob upload --account-name $StorageAccount --account-key $key -c public -n meta.json -f $metaFile `
  --content-type 'application/json' --content-cache-control 'public, max-age=900' --overwrite --output none
Remove-Item $metaFile -Force

Write-Host "==> Published. Installed copies stage v$version on their next HDT start."
Write-Host "    Reminder: create the GitHub release v$version with $([System.IO.Path]::GetFileName($zip)) attached."
