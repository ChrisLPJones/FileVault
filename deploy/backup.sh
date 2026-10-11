#!/usr/bin/env bash
# Backs up FileVault (database + encrypted files) to a restic repository. See docs/DEPLOYMENT.md, "Backups".
#
#   deploy/backup.sh            # one backup; run it daily from cron
#   deploy/backup.sh --init     # first run only: also creates the (empty) restic repository
#
# Settings come from a file that only exists on the server (default ~/filevault/backup.env, or
# FV_BACKUP_ENV): see deploy/backup.env.example. This script never reads .env, so the master key,
# the database passwords and the JWT key are never part of the backup.
#
# Order matters: the database goes first, then the files. Every row in the database backup then has
# its file (files are written once and never changed); a file newer than the database backup is only
# an orphan. No need to stop the stack: BACKUP DATABASE is online and consistent.
set -euo pipefail
umask 077

PROJECT="${COMPOSE_PROJECT:-filevault}"
ENV_FILE="${FV_BACKUP_ENV:-$HOME/filevault/backup.env}"
INIT=0
case "${1:-}" in
  "") ;;
  --init) INIT=1 ;;
  *) echo "usage: $0 [--init]" >&2; exit 2 ;;
esac

[ -f "$ENV_FILE" ] || { echo "backup settings file not found: $ENV_FILE (see deploy/backup.env.example)" >&2; exit 2; }
case "$(stat -c %a "$ENV_FILE")" in
  ?00) ;;
  *) echo "$ENV_FILE must not be readable by others: chmod 600 $ENV_FILE" >&2; exit 2 ;;
esac
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a
PROJECT="${COMPOSE_PROJECT:-$PROJECT}"
STATE_DIR="${FV_STATE_DIR:-$(dirname "$ENV_FILE")}"
RESTIC_IMAGE="${RESTIC_IMAGE:-restic/restic@sha256:8f5a62b422a2cb1277ea0dd6e826fe1acf649e5b9f02d60e5268d5fd1976255a}"
: "${RESTIC_REPOSITORY:?RESTIC_REPOSITORY is not set in $ENV_FILE}"
: "${RESTIC_PASSWORD_FILE:?RESTIC_PASSWORD_FILE is not set in $ENV_FILE}"
[ -f "$RESTIC_PASSWORD_FILE" ] || { echo "restic password file not found: $RESTIC_PASSWORD_FILE" >&2; exit 2; }
case "$(stat -c %a "$RESTIC_PASSWORD_FILE")" in ?00) ;; *) echo "$RESTIC_PASSWORD_FILE must not be readable by others: chmod 600" >&2; exit 2 ;; esac

mkdir -p "$STATE_DIR"
LOG="$STATE_DIR/backup.log"
# Keep the log small: one rotated copy
if [ -f "$LOG" ] && [ "$(stat -c %s "$LOG")" -gt 1000000 ]; then mv -f "$LOG" "$LOG.1"; fi
exec 9>"$STATE_DIR/backup.lock"
flock -n 9 || { echo "another backup is running" >&2; exit 3; }
exec > >(tee -a "$LOG") 2>&1

BAK_DIR=/var/opt/mssql/backup
BAK="$BAK_DIR/SecureVaultDb.bak"

# Healthchecks.io: optional. The URL goes in through stdin, not onto a command line.
ping_hc() { # $1 = suffix (/start, /fail or empty); body from stdin
  [ -n "${HC_PING_URL:-}" ] || { cat > /dev/null; return 0; }
  local body; body="$(mktemp)"
  cat > "$body"
  printf 'url = "%s%s"\ndata-binary = "@%s"\n' "${HC_PING_URL%/}" "$1" "$body" \
    | curl -fsS -m 15 --retry 3 -K - -o /dev/null 2>&1 || echo "(healthchecks ping failed)"
  rm -f "$body"
}

LAST_STEP="starting"
finish() {
  local rc=$?
  trap - EXIT
  if [ "$rc" -eq 0 ]; then
    echo "$(date -u +%FT%TZ) backup OK"
    echo "backup OK: $SUMMARY" | ping_hc ""
  else
    echo "$(date -u +%FT%TZ) backup FAILED (exit $rc) during: $LAST_STEP"
    { echo "backup FAILED (exit $rc) during: $LAST_STEP"; tail -n 15 "$LOG"; } | ping_hc "/fail"
  fi
  exit "$rc"
}
SUMMARY=""
trap finish EXIT
echo "$(date -u +%FT%TZ) backup starting (project $PROJECT)"
echo "" | ping_hc "/start"

SQL_CONT="$(docker ps -q --filter "label=com.docker.compose.project=$PROJECT" --filter "label=com.docker.compose.service=sqlserver" | head -n1)"
[ -n "$SQL_CONT" ] || { echo "no running sqlserver container in project $PROJECT" >&2; exit 1; }

# sqlcmd inside the container with the container's own sa password: nothing secret on a command line
sql() { docker exec -i "$SQL_CONT" sh -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" exec /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -h -1 -W "$@"' sh "$@"; }

restic() {
  local args=(run --rm -i
    -v "$RESTIC_PASSWORD_FILE:/run/restic-password:ro"
    -e RESTIC_PASSWORD_FILE=/run/restic-password
    -e RESTIC_REPOSITORY -e RESTIC_CACHE_DIR=/cache
    -v "${PROJECT}_restic_cache:/cache")
  [ -z "${B2_ACCOUNT_ID:-}" ] || args+=(-e B2_ACCOUNT_ID)
  [ -z "${B2_ACCOUNT_KEY:-}" ] || args+=(-e B2_ACCOUNT_KEY)
  case "$RESTIC_REPOSITORY" in /*) args+=(-v "$RESTIC_REPOSITORY:$RESTIC_REPOSITORY") ;; esac
  args+=("${EXTRA_DOCKER_ARGS[@]}")
  docker "${args[@]}" "$RESTIC_IMAGE" "$@"
}
EXTRA_DOCKER_ARGS=()

LAST_STEP="repository check"
if [ "$INIT" -eq 1 ]; then
  restic cat config > /dev/null 2>&1 && { echo "repository already exists" >&2; exit 1; }
  restic init
fi
restic cat config > /dev/null 2>&1 || { echo "cannot open the restic repository (wrong settings, or run once with --init)" >&2; exit 1; }

LAST_STEP="space check"
DB_MB="$(sql -Q "SET NOCOUNT ON; SELECT CAST(SUM(size) * 8 / 1024 AS INT) FROM sys.master_files WHERE database_id = DB_ID('SecureVaultDb')")"
FREE_MB="$(docker exec "$SQL_CONT" df -Pm /var/opt/mssql | awk 'NR==2 {print $4}')"
NEED_MB=$(( DB_MB * 2 > 2048 ? DB_MB * 2 : 2048 ))
echo "database ${DB_MB} MB, free ${FREE_MB} MB in the database volume (need ${NEED_MB} MB)"
[ "$FREE_MB" -ge "$NEED_MB" ] || { echo "not enough free space for the backup file" >&2; exit 1; }

LAST_STEP="reading counts"
COUNTS="$(sql -Q "SET NOCOUNT ON; SELECT 'users=' + CAST((SELECT COUNT(*) FROM Users) AS VARCHAR(20)) + ' files=' + CAST((SELECT COUNT(*) FROM Files) AS VARCHAR(20)) + ' shares=' + CAST((SELECT COUNT(*) FROM Shares) AS VARCHAR(20))" -d SecureVaultDb)"
# One-way tag: tells which master key opens the snapshot, reveals nothing about the key
MK="$(sql -Q "SET NOCOUNT ON; SELECT LEFT(KeyFingerprint, 12) FROM MasterKeyState" -d SecureVaultDb)"
[[ "$MK" =~ ^[0-9A-Fa-f]{12}$ ]] || { echo "could not read the key fingerprint" >&2; exit 1; }
echo "row counts: $COUNTS; key tag mk-$MK"
SUMMARY="$COUNTS"

LAST_STEP="database backup"
docker exec "$SQL_CONT" mkdir -p "$BAK_DIR"
sql -Q "BACKUP DATABASE SecureVaultDb TO DISK = N'$BAK' WITH CHECKSUM, INIT, FORMAT, STATS = 100" > /dev/null
LAST_STEP="database backup verify"
sql -Q "RESTORE VERIFYONLY FROM DISK = N'$BAK' WITH CHECKSUM" > /dev/null
echo "database backup written and verified"

# Both volumes read-only; only the .bak and the file folder are read from them
LAST_STEP="restic backup"
EXTRA_DOCKER_ARGS=(-v "${PROJECT}_sql_data:/sql:ro" -v "${PROJECT}_file_storage:/files:ro")
restic backup --host filevault --tag "mk-$MK" --tag "$(echo "$COUNTS" | tr ' ' ',' )" \
  --exclude-caches /sql/backup/SecureVaultDb.bak /files
EXTRA_DOCKER_ARGS=()

LAST_STEP="retention"
restic forget --host filevault --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune

if [ "$(date -u +%u)" = 7 ] || [ -n "${FV_FORCE_CHECK:-}" ]; then
  LAST_STEP="repository check (5% of the data)"
  restic check --read-data-subset=5%
fi
LAST_STEP="done"
