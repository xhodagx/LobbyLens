<#
  Publish/rotate the tip links shown in every installed plugin's
  Settings window. Merges into meta.json on the backend blob — no plugin release
  needed; installed copies pick it up on their next HDT start.

  Empty string CLEARS a link (hides its UI); omitted parameters are left unchanged.

  Usage:
    ./set-support.ps1 -Kofi 'https://ko-fi.com/<name>' -Lightning '<name>@strike.me' -Btc 'bc1...'
    ./set-support.ps1 -Btc ''          # remove just the on-chain address
#>
param(
  [string] $Kofi,
  [string] $Lightning,
  [string] $Btc,
  [string] $StorageAccount = 'stdatayififhlgyqepq',
  [string] $SubscriptionId   # defaults to the active az CLI subscription
)
$subArgs = if ($SubscriptionId) { @('--subscription', $SubscriptionId) } else { @() }

$ErrorActionPreference = 'Stop'

# light sanity checks — warn, don't block
if ($Kofi -and $Kofi -notmatch '^https://ko-fi\.com/\S+$') { Write-Warning "Ko-fi value doesn't look like https://ko-fi.com/<name>" }
if ($Lightning -and $Lightning -notmatch '^\S+@\S+\.\S+$') { Write-Warning "Lightning value doesn't look like a lightning address (name@domain)" }
if ($Btc -and $Btc -notmatch '^(bc1[a-z0-9]{20,}|[13][A-Za-z0-9]{25,})$') { Write-Warning "BTC value doesn't look like a bitcoin address" }

$metaUrl = "https://$StorageAccount.blob.core.windows.net/public/meta.json"
$meta = Invoke-RestMethod $metaUrl

foreach ($pair in @(@('Kofi', 'kofi'), @('Lightning', 'lightning'), @('Btc', 'btc'))) {
  $param, $name = $pair
  # [string] params default to '' (never $null), so "omitted" must be detected via
  # $PSBoundParameters — an explicit '' still clears, an omitted param leaves as-is.
  if (-not $PSBoundParameters.ContainsKey($param)) { continue }
  $value = Get-Variable $param -ValueOnly
  if ($meta.PSObject.Properties[$name]) { $meta.$name = $value }
  else { $meta | Add-Member -NotePropertyName $name -NotePropertyValue $value }
}

$file = Join-Path $env:TEMP 'lobbylens-meta.json'
$meta | ConvertTo-Json | Out-File $file -Encoding utf8
$key = az storage account keys list -n $StorageAccount @subArgs --query '[0].value' -o tsv
az storage blob upload --account-name $StorageAccount --account-key $key -c public -n meta.json -f $file `
  --content-type 'application/json' --content-cache-control 'public, max-age=900' --overwrite --output none
Remove-Item $file -Force

Write-Host '==> Published. Current support links:'
Write-Host "    kofi:      $($meta.kofi)"
Write-Host "    lightning: $($meta.lightning)"
Write-Host "    btc:       $($meta.btc)"
