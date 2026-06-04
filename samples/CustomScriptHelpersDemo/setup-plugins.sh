#!/usr/bin/env bash
# Populates ./plugins with a sample third-party DLL (Newtonsoft.Json), simulating the
# DLLs an operator would mount via a Docker volume. The host does NOT reference this package.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)/plugins"
mkdir -p "$DIR"

find_dll() {
  ls "$HOME"/.nuget/packages/newtonsoft.json/*/lib/netstandard2.0/Newtonsoft.Json.dll 2>/dev/null \
    | sort -V | tail -1 || true
}

SRC="$(find_dll)"
if [ -z "${SRC:-}" ]; then
  echo "Newtonsoft.Json not in the NuGet cache — fetching via a temp project..."
  TMP="$(mktemp -d)"
  (cd "$TMP" && dotnet new classlib -o p >/dev/null 2>&1 \
     && cd p && dotnet add package Newtonsoft.Json --version 13.0.3 >/dev/null 2>&1 \
     && dotnet restore >/dev/null 2>&1) || true
  rm -rf "$TMP"
  SRC="$(find_dll)"
fi

if [ -z "${SRC:-}" ]; then
  echo "ERROR: could not obtain Newtonsoft.Json.dll. Place any approved DLL into $DIR manually." >&2
  exit 1
fi

cp "$SRC" "$DIR/"
echo "Copied $(basename "$SRC") -> $DIR"
echo "Now run: dotnet run -c Release   (step [6] will load it dynamically)"
