#!/usr/bin/env bash

set -euo pipefail

# ICU versions to support (Debian has no virtual package, must list all)
ICU_VERSIONS="78 77 76 74 72 71 70 69 68 67 66 65 63"

arch=""
appimage_arch=""
target=""
case "$RUNTIME" in
    linux-x64)
        arch="amd64"
        appimage_arch="x86_64"
        target="x86_64"
        ;;
    linux-arm64)
        arch="arm64"
        appimage_arch="arm_aarch64"
        target="aarch64"
        ;;
    *)
        echo "Unknown runtime $RUNTIME"
        exit 1
        ;;
esac

APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"

cd build

if [[ ! -f "appimagetool" ]]; then
    curl -fL -o appimagetool "$APPIMAGETOOL_URL"
    chmod +x appimagetool
fi

rm -f GitNest/*.dbg GitNest/*.pdb GitNest/createdump
if [[ -f "GitNest/GitNest" ]]; then
    mv GitNest/GitNest GitNest/gitnest
fi

cp -f ../LICENSE ../NOTICE.md ../THIRD-PARTY-LICENSES.md GitNest/
mkdir -p GitNest/licenses
cp -f ../depends/AvaloniaEdit/LICENSE GitNest/licenses/AvaloniaEdit-LICENSE.txt
chmod 755 -R GitNest

# 1. Portable tar.gz
tar -czf "GitNest-$VERSION-$RUNTIME.tar.gz" -C GitNest .

# 2. AppImage
rm -rf GitNest.AppDir
mkdir -p GitNest.AppDir/opt
mkdir -p GitNest.AppDir/usr/share/metainfo
mkdir -p GitNest.AppDir/usr/share/applications

cp -r GitNest GitNest.AppDir/opt/gitnest
desktop-file-install resources/_common/applications/gitnest.desktop --dir GitNest.AppDir/usr/share/applications \
    --set-icon io.github.moakiee.gitnest --set-key=Exec --set-value=AppRun
mv GitNest.AppDir/usr/share/applications/gitnest.desktop GitNest.AppDir/usr/share/applications/io.github.moakiee.gitnest.desktop
cp resources/_common/icons/gitnest.png GitNest.AppDir/io.github.moakiee.gitnest.png
ln -rsf GitNest.AppDir/opt/gitnest/gitnest GitNest.AppDir/AppRun
ln -rsf GitNest.AppDir/usr/share/applications/io.github.moakiee.gitnest.desktop GitNest.AppDir/
cp resources/appimage/gitnest.appdata.xml GitNest.AppDir/usr/share/metainfo/io.github.moakiee.gitnest.appdata.xml

ARCH="$appimage_arch" ./appimagetool -v \
    GitNest.AppDir "GitNest-$VERSION-linux-$arch.AppImage"

# 3. Debian .deb
deb_version="${VERSION//-/\~}"
rm -rf resources/deb/opt resources/deb/usr
mkdir -p resources/deb/opt/gitnest
mkdir -p resources/deb/usr/bin
mkdir -p resources/deb/usr/share/applications
mkdir -p resources/deb/usr/share/icons
cp -r GitNest/* resources/deb/opt/gitnest/
ln -rsf resources/deb/opt/gitnest/gitnest resources/deb/usr/bin/gitnest
cp -r resources/_common/applications resources/deb/usr/share/
cp -r resources/_common/icons resources/deb/usr/share/

installed_size=$(du -sk resources/deb | cut -f1)
icu_deps="libicu"
for v in $ICU_VERSIONS; do
    icu_deps="$icu_deps | libicu$v"
done

sed -i -e "s/^Version:.*/Version: $deb_version/" \
    -e "s/^Architecture:.*/Architecture: $arch/" \
    -e "s/^Installed-Size:.*/Installed-Size: $installed_size/" \
    -e "s/@ICU_DEPS@/$icu_deps/" \
    resources/deb/DEBIAN/control

chmod 755 resources/deb/DEBIAN/preinst resources/deb/DEBIAN/postinst resources/deb/DEBIAN/prerm
dpkg-deb -Zgzip --root-owner-group --build resources/deb "GitNest-$VERSION-linux-$arch.deb"

# 4. RPM .rpm
rpm_version="${VERSION//-/\~}"
rpmbuild -bb --target="$target" resources/rpm/SPECS/build.spec \
    --define "_topdir $(pwd)/resources/rpm" \
    --define "_version $rpm_version" \
    --define "_tag_version $VERSION"
mv "resources/rpm/RPMS/$target/gitnest-$rpm_version-1.$target.rpm" "GitNest-$VERSION-linux-$target.rpm"
