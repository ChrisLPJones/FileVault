#!/bin/sh
# Creates/updates the schema and the API's database login once SQL Server is reachable, then exits.
# Runs as the entrypoint of the db-init image (see Dockerfile). Needs SQLCMDPASSWORD (the sa password)
# and FV_APP_PASSWORD (the API login's password) in the environment; FV_DB_HOST defaults to "sqlserver".
# Retries both scripts: the database can still be recovering right after a restart.
# sqlcmd reads $(FV_APP_PASSWORD) from the environment, so the password is not on a command line.
# The login password goes into a T-SQL literal and the API's connection string, so refuse characters that would break either.
case "$FV_APP_PASSWORD" in
  ""|*[\'\"\;\$\ ]*) echo "MSSQL_APP_PASSWORD must not be empty or contain quotes, semicolons, dollar signs or spaces" >&2; exit 1;;
esac
for i in 1 2 3 4 5 6 7 8 9 10; do
  /opt/mssql-tools18/bin/sqlcmd -C -S "${FV_DB_HOST:-sqlserver}" -U sa -b -i /db/init.sql \
    && /opt/mssql-tools18/bin/sqlcmd -C -S "${FV_DB_HOST:-sqlserver}" -U sa -b -i /db/app-login.sql -v FV_APP_LOGIN=filevault_app \
    && exit 0
  echo "database setup failed (attempt $i/10), retrying in 3s"
  sleep 3
done
echo "database setup failed; if the messages above mention the password, MSSQL_APP_PASSWORD must meet SQL Server's policy (8+ characters, three of: upper, lower, digit, symbol)" >&2
exit 1
