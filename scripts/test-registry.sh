#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ -z "${LFS_TEST_POSTGRES:-}" ]]; then
  container="lfs-registry-test-$(date +%s)-$$"
  trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
  docker run -d --rm --name "$container" -e POSTGRES_PASSWORD=disposable-test-password -p 127.0.0.1::5432 postgres:15-alpine >/dev/null
  for attempt in {1..60}; do
    if docker exec "$container" pg_isready -U postgres >/dev/null 2>&1; then break; fi
    sleep 1
  done
  port=$(docker port "$container" 5432/tcp)
  export LFS_TEST_POSTGRES="Host=127.0.0.1;Port=${port##*:};Username=postgres;Password=disposable-test-password;Database=postgres"
fi
dotnet test tests/Estranged.Lfs.Registry.Tests --configuration Release --verbosity minimal
