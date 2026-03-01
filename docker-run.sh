#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

echo "Building and starting .NET CLI server (port 8046)..."
docker compose up --build "$@"
