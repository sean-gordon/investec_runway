#!/bin/bash
set -e

# Ensure the entire app directory is owned by the app user
# This handles both volume mounts and build-time files
mkdir -p /app/keys /app/logs
chown -R app:app /app
chmod -R 755 /app/keys /app/logs

# Execute the application as the app user
echo "Starting GordonWorker as app user..."
exec gosu app dotnet GordonWorker.dll
