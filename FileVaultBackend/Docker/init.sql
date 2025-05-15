IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SecureVaultDb')
BEGIN
    CREATE DATABASE SecureVaultDb;
    PRINT 'Database "SecureVaultDb" created.';
END
ELSE
BEGIN
    PRINT 'Database "SecureVaultDb" already exists.';
END

DECLARE @file NVARCHAR(MAX) = '
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

EXEC(@file);

DECLARE @users NVARCHAR(MAX) = '
USE SecureVaultDb;

IF OBJECT_ID(''Users'', ''U'') IS NULL
BEGIN
    CREATE TABLE Users (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        Username VARCHAR(50) UNIQUE NOT NULL,
        Email VARCHAR(100) UNIQUE NOT NULL,
        PasswordHash VARCHAR(255) NOT NULL,
        CreatedAt DATETIME DEFAULT GETDATE(),
        LastLogin DATETIME,
        Role VARCHAR(20) DEFAULT ''user''
    );
    PRINT ''Table "Users" created.'';
END
ELSE
BEGIN
    PRINT ''Table "Users" already exists.'';
END
';

EXEC(@users);