#!/bin/sh
set -eu
cd "$(dirname "$0")"
umask 077
command -v openssl >/dev/null 2>&1 || { echo 'OpenSSL is required to generate random secrets.' >&2; exit 1; }
task_uid=$(id -u)
task_gid=$(id -g)
if [ "$task_uid" = 0 ]; then task_uid=1654; task_gid=1654; fi
if [ -f .env ]; then
    task_uid=$(awk -F= '$1 == "CLEARPLATE_UID" {print $2}' .env)
    task_gid=$(awk -F= '$1 == "CLEARPLATE_GID" {print $2}' .env)
fi
case "$task_uid:$task_gid" in *[!0-9:]*|:*|*:) echo 'Invalid UID/GID in .env.' >&2; exit 1;; esac
[ "$task_uid" -gt 0 ] && [ "$task_gid" -gt 0 ] || { echo 'Run the service as a non-root user.' >&2; exit 1; }
mkdir -p .secrets data
chmod 700 .secrets data
for task_secret in session_key admin_password; do
    if [ -e ".secrets/$task_secret" ] && [ ! -s ".secrets/$task_secret" ]; then
        echo 'An empty secret already exists; refusing to rotate or replace it.' >&2; exit 1
    fi
done
if [ ! -e .secrets/session_key ]; then
    if [ -e data/state.enc ]; then echo 'Existing encrypted state has no key; restore the original key instead of generating one.' >&2; exit 1; fi
    openssl rand -base64 32 > .secrets/session_key
fi
if [ ! -e .secrets/admin_password ]; then openssl rand -hex 24 > .secrets/admin_password; fi
chmod 600 .secrets/session_key .secrets/admin_password
if [ ! -f .env ]; then
    printf 'CLEARPLATE_UID=%s\nCLEARPLATE_GID=%s\nCLEARPLATE_PORT=8787\nCLEARPLATE_PUBLIC_ORIGIN=http://localhost:8787\n' "$task_uid" "$task_gid" > .env
fi
chmod 600 .env
if [ "$(id -u)" = 0 ]; then
    chown "$task_uid:$task_gid" .secrets data .secrets/session_key .secrets/admin_password
elif [ "$(id -u)" != "$task_uid" ] || [ "$(id -g)" != "$task_gid" ]; then
    echo 'UID/GID differ from the deployment owner; correct .env and directory ownership before starting.' >&2; exit 1
fi
printf '%s\n' 'Initialized without displaying secrets.' 'Administrator password: .secrets/admin_password' 'Start with: docker compose up -d --build' 'Default access: SSH tunnel to localhost:8787.'
