#!/usr/bin/env bash
# Turn a `dotnet publish` output directory into a zipped Cutback.app bundle.
#
#   scripts/package-macos.sh <publish-dir> <version> <out-dir>
#
# Produces <out-dir>/Cutback-<version>-osx-arm64.zip containing Cutback.app, LICENSE and
# THIRD-PARTY-NOTICES.md. The bundle is ad-hoc signed (required for native code on Apple
# Silicon) but not notarised, so Gatekeeper will still quarantine the download. See README.
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "usage: $0 <publish-dir> <version> <out-dir>" >&2
  exit 2
fi

publish_dir=$1
version=$2
out_dir=$3
short_version=${version%%-*}   # 1.2.3-beta.1 -> 1.2.3; CFBundleShortVersionString must be X.Y.Z

repo=$(cd "$(dirname "$0")/.." && pwd)
name="Cutback-$version-osx-arm64"
stage="$out_dir/$name"
app="$stage/Cutback.app"

if [[ ! -x "$publish_dir/Cutback" ]]; then
  echo "error: $publish_dir/Cutback not found or not executable" >&2
  exit 1
fi

rm -rf "$stage"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out_dir"

cp -R "$publish_dir"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Cutback"

# Only code may live in Contents/MacOS: codesign rejects the bundle if the ggml Metal shader
# source sits there. Resources is also where ggml's [NSBundle mainBundle] lookup finds it.
find "$app/Contents/MacOS" -maxdepth 1 -name '*.metal' -exec mv {} "$app/Contents/Resources/" \;

sed -e "s/__VERSION__/$short_version/g" "$repo/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
plutil -lint "$app/Contents/Info.plist"

cp "$repo/LICENSE" "$repo/THIRD-PARTY-NOTICES.md" "$stage/"

# Ad-hoc sign every native library first, then the bundle itself (avoids the deprecated --deep).
find "$app/Contents/MacOS" -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - "$app"
codesign --verify --verbose=2 "$app"

zip="$out_dir/$name.zip"
rm -f "$zip"
ditto -c -k --sequesterRsrc --keepParent "$stage" "$zip"
rm -rf "$stage"
echo "Wrote $zip"
