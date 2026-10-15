#!/usr/bin/env bash
# CI diagnostic only: polls the test SQL Server for admin-lock stalls while the backend tests run.
# Usage: lock-poll.sh <sqlserver container id> <output file>   (password in env SA_PASSWORD)
# Read-only DMV queries on a separate connection every 2 s. Logs only: applock waits over 3 s,
# applock holders idle over 3 s (FvTest_ databases, not fv-api-running), plus a heartbeat,
# docker stats and the load average every ~30 s.
set +x
container="$1"
out="$2"

read -r -d '' QUERY <<'SQL'
SET NOCOUNT ON;
SELECT CONVERT(varchar(23), GETDATE(), 121) AS ts, DB_NAME(l.resource_database_id) AS db,
       l.request_session_id AS sid, l.request_status AS st, l.resource_description AS res,
       s.status AS sstatus, s.open_transaction_count AS otc, s.last_request_end_time AS last_end,
       r.blocking_session_id AS blk, r.wait_type AS wt, r.wait_time AS wtime,
       REPLACE(REPLACE(LEFT(t.text, 160), CHAR(13), ' '), CHAR(10), ' ') AS txt
FROM sys.dm_tran_locks l
JOIN sys.dm_exec_sessions s ON s.session_id = l.request_session_id
LEFT JOIN sys.dm_exec_requests r ON r.session_id = l.request_session_id
LEFT JOIN sys.dm_exec_connections c ON c.session_id = l.request_session_id
OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
WHERE l.resource_type = 'APPLICATION'
  AND l.resource_description NOT LIKE '%fv-api-running%'
  AND DB_NAME(l.resource_database_id) LIKE 'FvTest[_]%'
  AND ((l.request_status = 'WAIT' AND r.wait_time > 3000)
    OR (l.request_status = 'GRANT' AND r.session_id IS NULL
        AND s.last_request_end_time < DATEADD(second, -3, GETDATE())));
SQL

sql() {
  docker exec "$container" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" \
    -W -s'|' -h-1 -Q "$1" 2>&1
}

n=0
while true; do
  if [ $((n % 15)) -eq 0 ]; then
    {
      echo "[$(date -u +%T)] heartbeat load: $(cut -d' ' -f1-3 /proc/loadavg)"
      docker stats --no-stream --format '  sqlserver cpu={{.CPUPerc}} mem={{.MemUsage}} blockio={{.BlockIO}}' "$container" 2>&1
    } >> "$out"
  fi
  rows=$(sql "$QUERY" | grep -v '^$')
  [ -n "$rows" ] && printf '%s\n' "$rows" >> "$out"
  n=$((n + 1))
  sleep 2
done
