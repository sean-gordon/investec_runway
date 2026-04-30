#!/bin/bash
set -e

# Ensure the keys and logs directories exist and are owned by the app user
# This is necessary because volume mounts often have root ownership
mkdir -p /app/keys
mkdir -p /app/logs
chown -R app:app /app/keys
chown -R app:app /app/logs

# Execute the application as the app user
exec gosu app dotnet GordonWorker.dll
