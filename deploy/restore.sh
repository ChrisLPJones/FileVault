#!/usr/bin/env bash
# Restores a FileVault backup made by deploy/backup.sh into a NEW, separate Docker Compose project.
# See docs/DEPLOYMENT.md, "Restoring" and "Test restore".
#
#   deploy/restore.sh --project fvrestore --env-file restore.env [--snapshot ID] \
#       [--backup-env ~/filevault/backup.env] [--compose-file docker-compose.yml]... [--build]
#   deploy/restore.sh --project fvrestore --env-file restore.env --verify-only
#
# --project   name of the stack to restore into. Required. The production project ("filevault", or
#             FV_PRODUCTION_PROJECT) is always refused, and so is a project whose volumes already exist
#             (remove it first with: docker compose -p <name> down -v).
# --env-file  the stack's .env for the restored copy (all secrets). ENCRYPTION_MASTER_KEY must be the key
#             the snapshot was made with (the snapshot's tag "mk-..." starts the key's fingerprint).
# --compose-file can be repeated (e.g. an override file that moves the published ports).
# Ports: every published port must be bound to 127.0.0.1 (checked before anything is restored; otherwise the
#             empty stack is removed and the script stops): pass an override --compose-file.
# --build     for the source-build compose file (builds the images).
# Never touches the production stack's containers or volumes: every docker command names the project.
set -euo pipefail
umask 077

PROJECT=""; STACK_ENV=""; SNAPSHOT="latest"; BACKUP_ENV="${FV_BACKUP_ENV:-$HOME/filevault/backup.env}"
COMPOSE_FILES=(); BUILD=(); VERIFY_ONLY=0
usage() { sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }
while [ $# -gt 0 ]; do
  case "$1" in
    --project) PROJECT="${2:-}"; shift 2 ;;
    --env-file) STACK_ENV="${2:-}"; shift 2 ;;
    --snapshot) SNAPSHOT="${2:-}"; shift 2 ;;
    --backup-env) BACKUP_ENV="${2:-}"; shift 2 ;;
    --compose-file) COMPOSE_FILES+=(-f "${2:-}"); shift 2 ;;
    --build) BUILD=(--build); shift ;;
    --verify-only) VERIFY_ONLY=1; shift ;;
    *) usage ;;
  esac
done

[ -n "$PROJECT" ] || { echo "--project is required (the name of the NEW stack, never the production one)" >&2; exit 2; }
[[ "$PROJECT" =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo "invalid project name" >&2; exit 2; }
if [ "$PROJECT" = filevault ] || [ "$PROJECT" = "${FV_PRODUCTION_PROJECT:-filevault}" ]; then
  echo "refusing to restore into the production project '$PROJECT'. Restore into a separate project, e.g. fvrestore." >&2
  exit 2
fi
if [ -z "$STACK_ENV" ] || [ ! -f "$STACK_ENV" ]; then echo "--env-file is required and must exist" >&2; exit 2; fi
case "$(stat -c %a "$STACK_ENV")" in ?00) ;; *) echo "$STACK_ENV must not be readable by others: chmod 600" >&2; exit 2 ;; esac
[ ${#COMPOSE_FILES[@]} -gt 0 ] || COMPOSE_FILES=(-f docker-compose.yml)

dc() { docker compose -p "$PROJECT" "${COMPOSE_FILES[@]}" --env-file "$STACK_ENV" "$@"; }
sql_cont() { docker ps -q --filter "label=com.docker.compose.project=$PROJECT" --filter "label=com.docker.compose.service=sqlserver" | head -n1; }
sql() { docker exec -i "$(sql_cont)" sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -h -1 -W "$@"' sh "$@"; }

if [ "$VERIFY_ONLY" -eq 0 ]; then
  for v in sql_data file_storage; do
    if docker volume inspect "${PROJECT}_$v" > /dev/null 2>&1; then
      echo "volume ${PROJECT}_$v already exists. Remove the old test stack first: docker compose -p $PROJECT down -v" >&2
      exit 2
    fi
  done
  [ -f "$BACKUP_ENV" ] || { echo "backup settings file not found: $BACKUP_ENV" >&2; exit 2; }
  case "$(stat -c %a "$BACKUP_ENV")" in ?00) ;; *) echo "$BACKUP_ENV must not be readable by others: chmod 600" >&2; exit 2 ;; esac
  # Read only the variables we need, in a subshell: the file cannot change PROJECT, STACK_ENV, COMPOSE_FILES etc.
  # shellcheck disable=SC1090
  eval "$( ( set +u; . "$BACKUP_ENV"
    for n in RESTIC_REPOSITORY RESTIC_PASSWORD_FILE B2_ACCOUNT_ID B2_ACCOUNT_KEY RESTIC_IMAGE; do
      printf 'export %s=%q\n' "$n" "${!n:-}"; done ) )"
  : "${RESTIC_REPOSITORY:?RESTIC_REPOSITORY is not set in $BACKUP_ENV}"
  : "${RESTIC_PASSWORD_FILE:?RESTIC_PASSWORD_FILE is not set in $BACKUP_ENV}"
  [ -f "$RESTIC_PASSWORD_FILE" ] || { echo "restic password file not found: $RESTIC_PASSWORD_FILE" >&2; exit 2; }
  case "$(stat -c %a "$RESTIC_PASSWORD_FILE")" in ?00) ;; *) echo "$RESTIC_PASSWORD_FILE must not be readable by others: chmod 600" >&2; exit 2 ;; esac
  RESTIC_IMAGE="${RESTIC_IMAGE:-restic/restic@sha256:8f5a62b422a2cb1277ea0dd6e826fe1acf649e5b9f02d60e5268d5fd1976255a}"

  echo "creating the empty stack '$PROJECT' (volumes ${PROJECT}_sql_data, ${PROJECT}_file_storage)"
  dc up --no-start "${BUILD[@]}"

  # The copy must never listen on a public address: every published port has to be bound to 127.0.0.1
  # (use an extra --compose-file that sets  ports: !override ["127.0.0.1:3101:8080"]  ). Nothing is restored yet.
  for c in $(docker ps -aq --filter "label=com.docker.compose.project=$PROJECT"); do
    bindings="$(docker inspect --format '{{json .HostConfig.PortBindings}}' "$c")" || { echo "cannot inspect container $c" >&2; dc down -v > /dev/null 2>&1 || true; exit 2; }
    if printf '%s' "$bindings" | grep -o '"HostIp":"[^"]*"' | grep -qv '"HostIp":"127.0.0.1"'; then
      echo "a port of this stack is not bound to 127.0.0.1. Add a --compose-file that publishes api/frontend on 127.0.0.1 only (see docs/DEPLOYMENT.md). Removing the empty stack." >&2
      dc down -v > /dev/null 2>&1 || true
      exit 2
    fi
  done

  echo "restoring snapshot '$SNAPSHOT' into its volumes"
  args=(run --rm
    -v "$RESTIC_PASSWORD_FILE:/run/restic-password:ro" -e RESTIC_PASSWORD_FILE=/run/restic-password
    -e RESTIC_REPOSITORY
    -v "${PROJECT}_sql_data:/restore/sql" -v "${PROJECT}_file_storage:/restore/files")
  [ -z "${B2_ACCOUNT_ID:-}" ] || args+=(-e B2_ACCOUNT_ID)
  [ -z "${B2_ACCOUNT_KEY:-}" ] || args+=(-e B2_ACCOUNT_KEY)
  case "$RESTIC_REPOSITORY" in /*) args+=(-v "$RESTIC_REPOSITORY:$RESTIC_REPOSITORY:ro") ;; esac
  docker "${args[@]}" "$RESTIC_IMAGE" restore "$SNAPSHOT" --no-lock --target /restore
  docker "${args[@]}" "$RESTIC_IMAGE" snapshots --no-lock --compact "$SNAPSHOT" || true

  echo "starting SQL Server"
  dc up -d sqlserver
  for _ in $(seq 1 60); do
    [ "$(docker inspect --format '{{.State.Health.Status}}' "$(sql_cont)" 2> /dev/null || true)" = healthy ] && break
    sleep 5
  done
  [ "$(docker inspect --format '{{.State.Health.Status}}' "$(sql_cont)")" = healthy ] || { echo "SQL Server did not become healthy" >&2; exit 1; }

  echo "restoring the database"
  sql -Q "RESTORE DATABASE SecureVaultDb FROM DISK = N'/var/opt/mssql/backup/SecureVaultDb.bak' WITH CHECKSUM, REPLACE, STATS = 100" > /dev/null

  echo "starting the stack (db-init upgrades the schema and re-links the API login)"
  dc up -d "${BUILD[@]}"
  echo "waiting 25 s to see whether the API accepts ENCRYPTION_MASTER_KEY"
  sleep 25
  api_id="$(dc ps -q api)"
  restarts="$(docker inspect --format '{{.RestartCount}}' "$api_id" 2> /dev/null || echo 1)"
  api_logs="$(docker logs "$api_id" 2>&1 || true)"
  if [ -z "$(dc ps --status running --services | grep -x api || true)" ] || [ "$restarts" != 0 ] || grep -q "does not match" <<< "$api_logs"; then
    echo "the API is not healthy (restarts: $restarts). A 'does not match' message means ENCRYPTION_MASTER_KEY is not the snapshot's key. Last log lines:" >&2
    dc logs --tail 8 api >&2 || true
    exit 1
  fi
fi

echo "== verification =="
sql -d SecureVaultDb -Q "SET NOCOUNT ON; SELECT 'users=' + CAST((SELECT COUNT(*) FROM Users) AS VARCHAR(20)) + ' files=' + CAST((SELECT COUNT(*) FROM Files) AS VARCHAR(20)) + ' shares=' + CAST((SELECT COUNT(*) FROM Shares) AS VARCHAR(20)) + ' key=mk-' + (SELECT LEFT(KeyFingerprint, 12) FROM MasterKeyState)"
echo "compare with the counts and the mk- tag in the snapshot's tags (restic snapshots) / backup.log"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
sql -d SecureVaultDb -Q "SET NOCOUNT ON; SELECT GUID FROM Files WHERE IsDirectory = 0" | sort > "$tmp/db"
docker run --rm -v "${PROJECT}_file_storage:/files:ro" alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc ls -1 /files | sort > "$tmp/disk"
echo "files in the database: $(wc -l < "$tmp/db"), on disk: $(wc -l < "$tmp/disk")"
echo "missing from disk (in the database, no file): $(comm -23 "$tmp/db" "$tmp/disk" | wc -l)"
comm -23 "$tmp/db" "$tmp/disk" | head -n 20
echo "on disk but not in Files (thumbnails, avatars, abandoned uploads, files newer than the database backup): $(comm -13 "$tmp/db" "$tmp/disk" | wc -l)"
echo "Next: sign in to the restored copy, download a file and compare it with the original."
echo "Remove the test stack when done: docker compose -p $PROJECT down -v"
