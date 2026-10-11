# FileVault db-init: creates/updates the schema and the API's database login, then exits.
# Build context is this directory (Backend/.dockerignore keeps Docker/ out of the API image):
#   docker build -t filevault-db-init ./Backend/Docker/db
# Holds only the SQL scripts and db-init.sh; the passwords come from the environment at run time.
FROM mcr.microsoft.com/mssql/server:2022-latest
COPY init.sql app-login.sql /db/
COPY --chmod=755 db-init.sh /usr/local/bin/db-init.sh
ENTRYPOINT ["/usr/local/bin/db-init.sh"]
