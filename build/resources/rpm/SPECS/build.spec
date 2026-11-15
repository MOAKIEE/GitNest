Name: gitnest
Version: %_version
Release: 1
Summary: Git GUI Client for AI-assisted coding
License: MIT
URL: https://github.com/MOAKIEE/GitNest
Source: https://github.com/MOAKIEE/GitNest/archive/refs/tags/v%_tag_version.tar.gz
Requires: libX11.so.6()(%{__isa_bits}bit)
Requires: libSM.so.6()(%{__isa_bits}bit)
Requires: libicu
Requires: xdg-utils

%define _build_id_links none

%description
Git GUI Client for AI-assisted coding, based on SourceGit.

%install
mkdir -p %{buildroot}/opt/gitnest
mkdir -p %{buildroot}/%{_bindir}
mkdir -p %{buildroot}/usr/share/applications
mkdir -p %{buildroot}/usr/share/icons
cp -r %{_topdir}/../../GitNest/* %{buildroot}/opt/gitnest/
ln -rsf %{buildroot}/opt/gitnest/gitnest %{buildroot}/%{_bindir}/gitnest
cp -r %{_topdir}/../_common/applications %{buildroot}/%{_datadir}
cp -r %{_topdir}/../_common/icons %{buildroot}/%{_datadir}
chmod 755 %{buildroot}/%{_datadir}/applications/gitnest.desktop

%files
%dir /opt/gitnest/
/opt/gitnest/*
/usr/share/applications/gitnest.desktop
/usr/share/icons/*
%{_bindir}/gitnest

%changelog
# skip
