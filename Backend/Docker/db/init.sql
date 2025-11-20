-- Create database if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SecureVaultDb')
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

-- Create Users table if it doesn't exist
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users')
BEGIN
    CREATE TABLE Users (
        Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
        Username VARCHAR(50) UNIQUE NOT NULL,
        Email VARCHAR(100) UNIQUE NOT NULL,
        PasswordHash VARCHAR(255) NOT NULL,
        CreatedAt DATETIME DEFAULT GETDATE(),
        LastLogin DATETIME,
        Role VARCHAR(20) DEFAULT 'user'
    );
    PRINT 'Table "Users" created.';
END
ELSE
BEGIN
    PRINT 'Table "Users" already exists.';
END
GO

-- Create Files table if it doesn't exist
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Files')
BEGIN
    CREATE TABLE Files (
        Id INT IDENTITY(1,1) NOT NULL,
        FileName NVARCHAR(50) NOT NULL,
        IsDirectory BIT NOT NULL,
        FilePath NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME DEFAULT GETDATE(),
        GUID NVARCHAR(50) NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,
        Size BIGINT NOT NULL,
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

-- === Optional: Add/Remove future columns safely ===
--
-- Add 'ProfilePictureUrl' to Users if it doesn't exist
--IF NOT EXISTS (
--    SELECT * FROM INFORMATION_SCHEMA.COLUMNS 
--    WHERE TABLE_NAME = 'Users' AND COLUMN_NAME = 'ProfilePictureUrl'
--)
--BEGIN
--    ALTER TABLE Users ProfilePictureUrl VARCHAR(255) NULL;
--    PRINT 'Column "ProfilePictureUrl" added to Users.';
--END
--ELSE
--BEGIN
--    PRINT 'Column "ProfilePictureUrl" already exists.';
--END
--GO
--
---- Remove 'FileType' to Files if it doesn't exist
--IF EXISTS (
--    SELECT * FROM INFORMATION_SCHEMA.COLUMNS 
--    WHERE TABLE_NAME = 'Files' AND COLUMN_NAME = 'FileType'
--)
--BEGIN
--    ALTER TABLE Files DROP column FileType;
--    PRINT 'Column "FileType" dropped from Files.';
--END
--ELSE
--BEGIN
--    PRINT 'Column "FileType" already exists.';
--END
--GO
