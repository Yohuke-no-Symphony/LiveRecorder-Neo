#!/usr/bin/env bash
set -euo pipefail

runtime="${1:-linux-x64}"
build_webui=true
if [[ "${2:-}" == "--no-webui-build" ]]; then
    build_webui=false
fi
case "$runtime" in
    linux-x64|linux-arm64|win-x64|osx-x64|osx-arm64) ;;
    *) echo "Unsupported runtime: $runtime" >&2; exit 2 ;;
esac

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
source_root="$repo_root/Sources"
project_path="$source_root/Yohuke.LiveRecorder.Server/Yohuke.LiveRecorder.Server.csproj"
version="$(dotnet msbuild "$project_path" -nologo -p:Configuration=Release -p:RuntimeIdentifier="$runtime" -getProperty:Version)"
version="${version%$'\r'}"
if [[ ! "$version" =~ ^[0-9A-Za-z][0-9A-Za-z.+-]*$ ]]; then
    echo "Invalid release version: $version" >&2
    exit 2
fi

version_directory="$repo_root/Artifacts/server/$version"
output_directory="$version_directory/$runtime"
archive_path="$version_directory/server-$version-$runtime.zip"
command -v zip >/dev/null
dotnet publish "$project_path" \
    --configuration Release --runtime "$runtime" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded \
    -p:RuntimeFrameworkVersion=10.0.13 \
    -p:Version="$version" \
    -p:BuildWebUI="$build_webui" \
    --output "$output_directory"
cp "$repo_root/LICENSE" "$output_directory/LICENSE"
cp "$source_root/README.md" "$output_directory/README.md"
cp "$source_root/ThirdParty/BililiveRecorder.Flv/NOTICE" "$output_directory/THIRD-PARTY-NOTICES.txt"

archive_directory="$(mktemp -d "$version_directory/.zip.XXXXXXXX")"
trap 'rm -rf -- "$archive_directory"' EXIT
(
    cd -- "$output_directory"
    zip -q -r "$archive_directory/package.zip" .
)
mv -f -- "$archive_directory/package.zip" "$archive_path"
echo "Created $archive_path"
