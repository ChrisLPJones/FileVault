IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SecureVaultDb')
BEGIN
    CREATE DATABASE SecureVaultDb;
    PRINT 'Database "SecureVaultDb" created.';
END
ELSE
BEGIN
    PRINT 'Database "SecureVaultDb" already exists.';
END

DECLARE @sql NVARCHAR(MAX) = '
USE SecureVaultDb;

IF OBJECT_ID(''Files'', ''U'') IS NULL
BEGIN
    CREATE TABLE Files (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        FileName NVARCHAR(50),
        GUID NVARCHAR(50)
    );
    PRINT ''Table "Files" created.'';
END
ELSE
BEGIN
    PRINT ''Table "Files" already exists.'';
END
';

EXEC(@sql);