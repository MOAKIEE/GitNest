#!/usr/bin/env bash

set -euo pipefail

cd build

rm -rf GitNest.app
mkdir -p GitNest.app/Contents/Resources
mv GitNest GitNest.app/Contents/MacOS

cp -f ../LICENSE ../NOTICE.md ../THIRD-PARTY-LICENSES.md GitNest.app/Contents/Resources/
mkdir -p GitNest.app/Contents/Resources/licenses
cp -f ../depends/AvaloniaEdit/LICENSE GitNest.app/Contents/Resources/licenses/AvaloniaEdit-LICENSE.txt

# Generate App.icns from 1024x1024 PNG using macOS iconutil
iconset_dir="GitNest.iconset"
rm -rf "$iconset_dir"
mkdir -p "$iconset_dir"
src_icon="resources/app/icon_1024.png"
sips -z 16 16     "$src_icon" --out "$iconset_dir/icon_16x16.png" >/dev/null
sips -z 32 32     "$src_icon" --out "$iconset_dir/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$src_icon" --out "$iconset_dir/icon_32x32.png" >/dev/null
sips -z 64 64     "$src_icon" --out "$iconset_dir/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$src_icon" --out "$iconset_dir/icon_128x128.png" >/dev/null
sips -z 256 256   "$src_icon" --out "$iconset_dir/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$src_icon" --out "$iconset_dir/icon_256x256.png" >/dev/null
sips -z 512 512   "$src_icon" --out "$iconset_dir/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$src_icon" --out "$iconset_dir/icon_512x512.png" >/dev/null
cp "$src_icon"    "$iconset_dir/icon_512x512@2x.png"
iconutil -c icns "$iconset_dir" -o GitNest.app/Contents/Resources/App.icns
rm -rf "$iconset_dir"

numeric_version="${VERSION%%[-+]*}"
sed -e "s/GITNEST_NUMERIC_VERSION/${numeric_version}.0/g" \
    -e "s/GITNEST_VERSION/$VERSION/g" \
    resources/app/App.plist > GitNest.app/Contents/Info.plist

rm -rf GitNest.app/Contents/MacOS/*.dsym GitNest.app/Contents/MacOS/*.pdb GitNest.app/Contents/MacOS/createdump

case "$RUNTIME" in
  osx-arm64) ARCH="arm64" ;;
  osx-x64)   ARCH="x86_64" ;;
  *) echo "Unsupported RUNTIME: $RUNTIME" >&2; exit 1 ;;
esac

clang -arch "$ARCH" ../tools/setsid-macos/setsid.c -o GitNest.app/Contents/MacOS/setsid -mmacosx-version-min=13.0
codesign --force --deep --sign - GitNest.app
codesign --verify --deep --strict --verbose=2 GitNest.app

ditto -c -k --sequesterRsrc --keepParent GitNest.app "GitNest-$VERSION-$RUNTIME.zip"
