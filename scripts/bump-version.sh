#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  printf 'Usage: make bump-version VERSION=<x.y.z>\n' >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
plugin="BTCPayServer.Plugins.Boltz.Arkade"
project="$repo_root/$plugin/$plugin.csproj"
changelog="$repo_root/CHANGELOG.md"
cargo_manifest="$repo_root/BoltzClientBindings/Cargo.toml"
cargo_lock="$repo_root/BoltzClientBindings/Cargo.lock"
release_notes="$repo_root/release-notes-template.md"

current_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project")"
if [[ "$version" == "$current_version" ]]; then
  printf 'Version is already %s.\n' "$version" >&2
  exit 1
fi
if ! grep -Fq "## [$version] -" "$changelog"; then
  printf 'Add a CHANGELOG.md entry for %s before bumping the version.\n' "$version" >&2
  exit 1
fi

sed -i "s#<Version>[^<]*</Version>#<Version>$version</Version>#" "$project"
sed -i "0,/^version = \"[^\"]*\"$/s//version = \"$version\"/" "$cargo_manifest"
sed -i "/^name = \"boltz-client-bindings\"$/,/^$/s/^version = \"[^\"]*\"/version = \"$version\"/" "$cargo_lock"
sed -Ei "s/v[0-9]+\.[0-9]+\.[0-9]+/v$version/" "$release_notes"

printf 'Bumped Boltz.Arkade from %s to %s.\n' "$current_version" "$version"
