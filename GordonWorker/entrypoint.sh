#!/bin/bash
set -e

# Ensure the keys directory exists and is owned by the app user
# This is necessary because volume mounts often have root ownership
mkdir -p /app/keys
chown -R app:app /app/keys

# Execute the application as the app user
exec gosu app dotnet GordonWorker.dll
