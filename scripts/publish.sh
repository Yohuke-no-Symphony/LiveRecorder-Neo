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
output_directory="$repo_root/Artifacts/$runtime"
dotnet publish "$source_root/Yohuke.LiveRecorder.Server/Yohuke.LiveRecorder.Server.csproj" \
    --configuration Release --runtime "$runtime" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=embedded \
    -p:RuntimeFrameworkVersion=10.0.13 \
    -p:BuildWebUI="$build_webui" \
    --output "$output_directory"
cp "$repo_root/LICENSE" "$output_directory/LICENSE"
cp "$source_root/README.md" "$output_directory/README.md"
cp "$source_root/ThirdParty/BililiveRecorder.Flv/NOTICE" "$output_directory/THIRD-PARTY-NOTICES.txt"
