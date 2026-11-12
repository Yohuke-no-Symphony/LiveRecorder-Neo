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

project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet publish "$project_root/Yohuke.LiveRecorder.Server/Yohuke.LiveRecorder.Server.csproj" \
    --configuration Release --runtime "$runtime" --self-contained true \
    -p:RuntimeFrameworkVersion=10.0.13 \
    -p:BuildWebUI="$build_webui" \
    --output "$project_root/artifacts/$runtime"
cp "$project_root/LICENSE" "$project_root/artifacts/$runtime/LICENSE"
cp "$project_root/README.md" "$project_root/artifacts/$runtime/README.md"
cp "$project_root/ThirdParty/BililiveRecorder.Flv/NOTICE" "$project_root/artifacts/$runtime/THIRD-PARTY-NOTICES.txt"
