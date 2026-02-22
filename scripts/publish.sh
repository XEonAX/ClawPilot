#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/.."
OUTPUT_DIR="$PROJECT_DIR/publish"

echo "Publishing ClawPilot..."
dotnet publish "$PROJECT_DIR/src/ClawPilot/ClawPilot.csproj" \
  -c Release \
  -o "$OUTPUT_DIR" \
  --self-contained false

echo "Published to: $OUTPUT_DIR"
echo "Run with: dotnet $OUTPUT_DIR/ClawPilot.dll"
