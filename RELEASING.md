# Releasing LobbyLens (maintainer notes)

## How the update channel works

HDT has no plugin auto-update, so LobbyLens ships its own. Once per HDT session the
plugin fetches `meta.json` from the backend blob; when it names a newer version, the
plugin downloads the release zip, verifies its RSA signature against the public key(s)
baked into `Updater.cs` (an unsigned or tampered package is refused — the CDN is just a
pipe), and stages the files. The update applies on the next HDT restart. A remote
stand-down flag (`standDown` / `minVersion`) can pause match processing cleanly after a
breaking game patch — the updater keeps running, since it is the recovery path.

## Cutting a release

1. Bump `<Version>` in `LobbyLens/LobbyLens.csproj` (single source of truth — the plugin,
   the update check, and the package name all read it)
2. Test in HDT, commit, then run `./release.ps1` — it builds, zips, **signs the package
   with the offline release key**, uploads it, and updates `meta.json` (merging, so tip
   links survive). Every installed copy stages the update on its next HDT start.
3. Create the matching GitHub release with the zip attached (manual-download fallback).

## meta.json contract

`meta.json` is the remote control for shipped binaries — **additive fields only, never
rename or repurpose one**: `latest`/`url`/`pkg`/`sig`/`sha256` (update channel), `kofi`/
`lightning`/`btc` (Settings tip links; empty hides), `standDown`/`minVersion` (kill
switch), `ingest` (endpoint override). Rotate tip links with `./set-support.ps1` — both
scripts merge into the existing file, never blind-write it.

## Signing key

The private signing key lives outside the repo (`~\.lobbylens\signing\`) and must never
be committed or uploaded; losing it means old binaries can no longer auto-update (they
fall back to the manual notice), so back it up. Key rotation: add the new public key to
`Updater.PublicKeys`, ship a release signed with the old key, then sign with the new one
from the next release on.
