#!/usr/bin/env bash
# Builds the installable package for one platform:
#   build/pack.sh linux-x64 2.1.0   ->  releases/linux/LighthouseManager.AppImage (+ Velopack update packages)
#   build/pack.sh win-x64 2.1.0     ->  releases/win/LighthouseManager-win-Setup.exe (+ update packages)
# Both can be built on Linux (Windows via vpk's cross-compiling). Needs: .NET 10 SDK, vpk (dotnet tool install -g vpk).
set -euo pipefail

rid=${1:?runtime identifier, linux-x64 or win-x64}
version=${2:?version, e.g. 2.1.0}
root=$(cd "$(dirname "$0")/.." && pwd)
publish="$root/publish/$rid"

case "$rid" in
  win-*) tfm=net10.0-windows10.0.19041.0; platform=win; icon=icon.ico; ext=.exe ;;
  linux-*) tfm=net10.0; platform=linux; icon=icon.png; ext= ;;
  *) echo "unsupported runtime $rid" >&2; exit 1 ;;
esac
output="$root/releases/$platform"

rm -rf "$publish"
common=(-c Release -r "$rid" -o "$publish" -p:Version="$version" --self-contained
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=None)

for project in LighthouseManager LighthouseManagerService; do
  dotnet publish "$root/$project" -f "$tfm" "${common[@]}"
done
# The tray app is a pure API client without Bluetooth, so it has a single TFM for both platforms
dotnet publish "$root/LighthouseManager.Tray" -f net10.0 "${common[@]}" -p:IncludeNativeLibrariesForSelfExtract=true

cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$publish/"

vpk "[$platform]" pack \
  --packId LighthouseManager \
  --packVersion "$version" \
  --packTitle LighthouseManager \
  --packAuthors SeeUnsharp \
  --packDir "$publish" \
  --mainExe "LighthouseManager.Tray$ext" \
  --icon "$root/assets/$icon" \
  --runtime "$rid" \
  --outputDir "$output" \
  $([ "$platform" = win ] && echo "--shortcuts StartMenuRoot") \
  $([ "$platform" = linux ] && echo "--categories Utility")

echo "Packages in $output:"
ls -la "$output"
