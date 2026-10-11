------------------------------------------------------------
-- LEAST-PRIVILEGE LOGIN FOR THE API
--
-- Run by the db-init container as sa, right after init.sql, on every
-- `docker compose up`. sqlcmd variables (passed with -v):
--   FV_APP_LOGIN     the login name (docker-compose.yml: filevault_app)
--   FV_APP_PASSWORD  its password (MSSQL_APP_PASSWORD in .env)
-- The values go into T-SQL literals, so they must not contain a quote;
-- Backend/Docker/db/db-init.sh refuses a password with one.
--
-- The login can read and write the data in the dbo schema and nothing else:
-- no DDL, not db_owner, not sysadmin. Safe to run repeatedly; changing the
-- password in .env changes it here on the next `up`.
------------------------------------------------------------
USE master;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$(FV_APP_LOGIN)')
BEGIN
    CREATE LOGIN [$(FV_APP_LOGIN)] WITH PASSWORD = N'$(FV_APP_PASSWORD)',
        CHECK_POLICY = ON, CHECK_EXPIRATION = OFF, DEFAULT_DATABASE = [SecureVaultDb];
    PRINT 'Login "$(FV_APP_LOGIN)" created.';
END
ELSE IF NOT EXISTS (SELECT 1 FROM sys.sql_logins
                    WHERE name = N'$(FV_APP_LOGIN)' AND PWDCOMPARE(N'$(FV_APP_PASSWORD)', password_hash) = 1)
BEGIN
    ALTER LOGIN [$(FV_APP_LOGIN)] WITH PASSWORD = N'$(FV_APP_PASSWORD)';
    PRINT 'Password of login "$(FV_APP_LOGIN)" updated.';
END
GO

-- Lets `rotate-master-key` see other connections to the database, so it refuses to rotate
-- while anything else is connected. On SQL Server 2022 this is the narrow permission
-- (dm_exec_sessions only); it does not allow reading data or changing anything.
GRANT VIEW SERVER PERFORMANCE STATE TO [$(FV_APP_LOGIN)];
GO

USE SecureVaultDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(FV_APP_LOGIN)')
    CREATE USER [$(FV_APP_LOGIN)] FOR LOGIN [$(FV_APP_LOGIN)];
ELSE
    ALTER USER [$(FV_APP_LOGIN)] WITH LOGIN = [$(FV_APP_LOGIN)]; -- re-link if the login was recreated
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'filevault_app_role' AND type = 'R')
    CREATE ROLE filevault_app_role;
GO

-- Covers tables added by later versions of init.sql
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO filevault_app_role;
GO

IF IS_ROLEMEMBER(N'filevault_app_role', N'$(FV_APP_LOGIN)') <> 1
    ALTER ROLE filevault_app_role ADD MEMBER [$(FV_APP_LOGIN)];
GO
