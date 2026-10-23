#!/usr/bin/env sh
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
dotnet build "$project_root/src/CFGBINManager/CFGBINManager.csproj" -c Release --nologo
dotnet pack "$project_root/src/CFGBINManager/CFGBINManager.csproj" -c Release --no-build --output "$project_root/dist" --nologo
