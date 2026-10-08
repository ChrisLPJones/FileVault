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
        FirstName NVARCHAR(50) NOT NULL,
        LastName NVARCHAR(50) NOT NULL,
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
        CreatedAt DATETIME NULL CONSTRAINT DF_Files_CreatedAt DEFAULT GETDATE(),
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
END
ELSE
BEGIN
    PRINT 'Table "Files" already exists.';
END
GO

-- Note: there is deliberately no self-referencing foreign key on ParentId.
-- SQL Server rejects ON DELETE CASCADE on a self-reference (error 1785), and a
-- plain FK would block deleting a folder before its children. The
-- TR_Files_RecursiveDelete trigger below removes descendants instead.


------------------------------------------------------------
-- ENCRYPTION KEY COLUMN (added to existing databases too)
------------------------------------------------------------
-- Per-file data key, encrypted with the API's master key. NULL for folders
-- and for files stored before encryption was added.
IF COL_LENGTH('Files', 'WrappedKey') IS NULL
BEGIN
    ALTER TABLE Files ADD WrappedKey NVARCHAR(200) NULL;
    PRINT 'Column "Files.WrappedKey" added.';
END
GO

------------------------------------------------------------
-- FILES INDEXES (added to existing databases too)
------------------------------------------------------------
-- Listing a user's files, summing their storage and checking names in a folder
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Files_UserId_ParentId' AND object_id = OBJECT_ID('Files'))
BEGIN
    CREATE INDEX IX_Files_UserId_ParentId ON Files (UserId, ParentId) INCLUDE (FileName, IsDirectory, Size);
    PRINT 'Index "IX_Files_UserId_ParentId" created.';
END

-- Walking folder trees (recursive queries and the delete trigger)
IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Files_ParentId' AND object_id = OBJECT_ID('Files'))
BEGIN
    CREATE INDEX IX_Files_ParentId ON Files (ParentId);
    PRINT 'Index "IX_Files_ParentId" created.';
END
GO

------------------------------------------------------------
-- FIRST/LAST NAME REPLACE USERNAME (migrates existing databases too)
------------------------------------------------------------
IF COL_LENGTH('Users', 'FirstName') IS NULL
BEGIN
    ALTER TABLE Users ADD FirstName NVARCHAR(50) NULL, LastName NVARCHAR(50) NULL;
    PRINT 'Columns "Users.FirstName/LastName" added.';
END
GO

IF COL_LENGTH('Users', 'Username') IS NOT NULL
BEGIN
    -- Existing accounts keep their old username as the first name
    EXEC('UPDATE Users SET FirstName = LEFT(Username, 50), LastName = '''' WHERE FirstName IS NULL');

    -- Drop the unique constraint on Username, then the column
    DECLARE @drop NVARCHAR(MAX) = N'';
    SELECT @drop += N'ALTER TABLE Users DROP CONSTRAINT ' + QUOTENAME(kc.name) + N';'
    FROM sys.key_constraints kc
    JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE kc.parent_object_id = OBJECT_ID('Users') AND c.name = 'Username' AND kc.type = 'UQ';
    EXEC sp_executesql @drop;
    ALTER TABLE Users DROP COLUMN Username;
    PRINT 'Column "Users.Username" removed.';
END

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'FirstName' AND is_nullable = 1)
BEGIN
    EXEC('ALTER TABLE Users ALTER COLUMN FirstName NVARCHAR(50) NOT NULL');
    EXEC('ALTER TABLE Users ALTER COLUMN LastName NVARCHAR(50) NOT NULL');
END
GO

------------------------------------------------------------
-- STORAGE QUOTA COLUMN (added to existing databases too)
------------------------------------------------------------
-- Per-user storage limit in bytes. NULL = the API's Storage:DefaultQuotaBytes.
IF COL_LENGTH('Users', 'StorageQuota') IS NULL
BEGIN
    ALTER TABLE Users ADD StorageQuota BIGINT NULL;
    PRINT 'Column "Users.StorageQuota" added.';
END
GO

------------------------------------------------------------
-- AVATAR COLUMNS (added to existing databases too)
------------------------------------------------------------
-- The image is stored encrypted on disk (StorageRoot/avatars/<UserId>);
-- these columns hold its wrapped key, size, type and when it last changed.
IF COL_LENGTH('Users', 'AvatarWrappedKey') IS NULL
BEGIN
    ALTER TABLE Users ADD
        AvatarWrappedKey NVARCHAR(200) NULL,
        AvatarSize BIGINT NULL,
        AvatarMimeType NVARCHAR(50) NULL,
        AvatarUpdatedAt DATETIME2 NULL;
    PRINT 'Avatar columns added to "Users".';
END
GO

------------------------------------------------------------
-- FILE CREATED DATE (added to existing databases too)
------------------------------------------------------------
IF COL_LENGTH('Files', 'CreatedAt') IS NULL
BEGIN
    ALTER TABLE Files ADD CreatedAt DATETIME NULL CONSTRAINT DF_Files_CreatedAt DEFAULT GETDATE();
    PRINT 'CreatedAt column added to "Files".';
END
GO

-- Items from before the column existed: the closest known date is when they last changed
UPDATE Files SET CreatedAt = UpdatedAt WHERE CreatedAt IS NULL;
GO

------------------------------------------------------------
-- REFRESH TOKENS
------------------------------------------------------------
-- Only a SHA-256 hash of each token is stored. Tokens are single-use:
-- each refresh revokes the old one and issues a new one.
IF NOT EXISTS (SELECT *
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = 'RefreshTokens')
BEGIN
    CREATE TABLE RefreshTokens
    (
        Id INT IDENTITY(1,1) NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        TokenHash CHAR(64) NOT NULL,
        ExpiresAt DATETIME2 NOT NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        RevokedAt DATETIME2 NULL,
        CONSTRAINT PK_RefreshTokens PRIMARY KEY CLUSTERED (Id ASC),
        CONSTRAINT UQ_RefreshTokens_TokenHash UNIQUE NONCLUSTERED (TokenHash),
        CONSTRAINT FK_RefreshTokens_Users FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_RefreshTokens_UserId ON RefreshTokens (UserId);
    PRINT 'Table "RefreshTokens" created.';
END
ELSE
BEGIN
    PRINT 'Table "RefreshTokens" already exists.';
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
GO

------------------------------------------------------------
-- IMAGE THUMBNAILS
------------------------------------------------------------
-- One row per image file that has been thumbnailed. The thumbnail itself is
-- stored encrypted on disk (StorageRoot/thumbnails/<file GUID>) and doesn't
-- count towards the quota. WrappedKey NULL means the image couldn't be
-- thumbnailed, so it isn't retried on every request. The API removes a file's
-- row after deleting the file. There's deliberately no ON DELETE CASCADE
-- foreign key or trigger: both deadlocked with concurrent deletes and the
-- recursive delete trigger. A row left behind is harmless (lookups join Files,
-- and GUIDs aren't reused).
IF NOT EXISTS (SELECT *
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME = 'FileThumbnails')
BEGIN
    CREATE TABLE FileThumbnails
    (
        FileGuid NVARCHAR(100) NOT NULL,
        WrappedKey NVARCHAR(200) NULL,
        Size BIGINT NULL,
        MimeType NVARCHAR(50) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FileThumbnails PRIMARY KEY CLUSTERED (FileGuid)
    );
    PRINT 'Table "FileThumbnails" created.';
END
ELSE
BEGIN
    PRINT 'Table "FileThumbnails" already exists.';
END
GO

-- Earlier development versions of this table had a cascading foreign key and a trigger
IF EXISTS (SELECT * FROM sys.foreign_keys WHERE name = 'FK_FileThumbnails_Files')
    ALTER TABLE FileThumbnails DROP CONSTRAINT FK_FileThumbnails_Files;
DROP TRIGGER IF EXISTS TR_Files_DeleteThumbnails;
GO

------------------------------------------------------------
-- FAVOURITES AND RECENT FILES (added to existing databases too)
------------------------------------------------------------
-- Starred items, and when a file was last previewed or downloaded (UTC)
IF COL_LENGTH('Files', 'Favourite') IS NULL
BEGIN
    ALTER TABLE Files ADD Favourite BIT NOT NULL CONSTRAINT DF_Files_Favourite DEFAULT 0;
    PRINT 'Column "Files.Favourite" added.';
END
GO

IF COL_LENGTH('Files', 'LastOpenedAt') IS NULL
BEGIN
    ALTER TABLE Files ADD LastOpenedAt DATETIME2 NULL;
    PRINT 'Column "Files.LastOpenedAt" added.';
END
GO
