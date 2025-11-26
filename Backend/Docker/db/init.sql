------------------------------------------------------------
-- CREATE DATABASE IF IT DOESN'T EXIST
------------------------------------------------------------
IF NOT EXISTS (SELECT *
FROM sys.databases
WHERE name = 'SecureVaultDb')
BEGIN
    CREATE DATABASE SecureVaultDb;
    PRINT 'Database "SecureVaultDb" created.';
END
ELSE
BEGIN
    PRINT 'Database "SecureVaultDb" already exists.';
END
GO

USE SecureVaultDb;
GO

------------------------------------------------------------
-- USERS TABLE
------------------------------------------------------------
IF NOT EXISTS (SELECT *
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = 'Users')
BEGIN
    CREATE TABLE Users
    (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        Username NVARCHAR(50) UNIQUE NOT NULL,
        Email NVARCHAR(100) UNIQUE NOT NULL,
        PasswordHash NVARCHAR(255) NOT NULL,
        CreatedAt DATETIME DEFAULT GETDATE(),
        LastLogin DATETIME,
        Role NVARCHAR(20) DEFAULT 'user'
    );
    PRINT 'Table "Users" created.';
END
ELSE
BEGIN
    PRINT 'Table "Users" already exists.';
END
GO

------------------------------------------------------------
-- DEFAULT ADMIN USER
------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM Users WHERE Username = 'admin')
BEGIN
    INSERT INTO Users (Username, Email, PasswordHash, Role)
    VALUES (
        'admin',
        'admin@example.com',
        '$2a$11$13snxdxtbMLMq.ih1sK9oOuV245raTKVflvA0/npenUsmXkGWVqvi',
        'admin'
    );
    PRINT 'Default admin user created.';
END
ELSE
BEGIN
    PRINT 'Default admin user already exists.';
END
GO

------------------------------------------------------------
-- FILES TABLE
------------------------------------------------------------
IF NOT EXISTS (SELECT *
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = 'Files')
BEGIN
    CREATE TABLE Files
    (
        Id INT IDENTITY(1,1) NOT NULL,
        FileName NVARCHAR(255) NOT NULL,
        IsDirectory BIT NOT NULL,
        FilePath NVARCHAR(MAX) NULL,
        UpdatedAt DATETIME DEFAULT GETDATE(),
        GUID NVARCHAR(100) NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        Size BIGINT NOT NULL,
        ParentId NVARCHAR(100) NULL,
        MimeType NVARCHAR(255) NULL,
        CONSTRAINT PK_Files PRIMARY KEY CLUSTERED (Id ASC),
        CONSTRAINT UQ_Files_GUID UNIQUE NONCLUSTERED (GUID ASC),
        CONSTRAINT FK_Files_Users FOREIGN KEY (UserId) REFERENCES Users(Id)
    );
    PRINT 'Table "Files" created.';

    ALTER TABLE Files
    ADD CONSTRAINT FK_Files_Parent
    FOREIGN KEY (ParentId) REFERENCES Files(GUID)
    ON DELETE CASCADE;

    PRINT 'Foreign key FK_Files_Parent added with ON DELETE CASCADE.';
END
ELSE
BEGIN
    PRINT 'Table "Files" already exists.';

    IF NOT EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_Files_Parent')
    BEGIN
        ALTER TABLE Files
        ADD CONSTRAINT FK_Files_Parent
        FOREIGN KEY (ParentId) REFERENCES Files(GUID)
        ON DELETE CASCADE;

        PRINT 'Foreign key FK_Files_Parent added with ON DELETE CASCADE.';
    END
END
GO


------------------------------------------------------------
-- RECURSIVE DELETE TRIGGER (FULL DEPTH CASCADE)
------------------------------------------------------------
CREATE OR ALTER TRIGGER TR_Files_RecursiveDelete
ON Files
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH RecursiveChildren AS (
        SELECT f.Id, f.GUID, f.ParentId
        FROM Files f
        INNER JOIN deleted d ON f.ParentId = d.GUID

        UNION ALL

        SELECT f2.Id, f2.GUID, f2.ParentId
        FROM Files f2
        INNER JOIN RecursiveChildren rc ON f2.ParentId = rc.GUID
    )
    DELETE FROM Files
    WHERE Id IN (SELECT Id FROM RecursiveChildren);
END
GO

PRINT 'Recursive delete trigger created.';
