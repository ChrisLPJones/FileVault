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

------------------------------------------------------------
-- USERS TABLE
------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users')
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
-- FILES TABLE
------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Files')
BEGIN
    CREATE TABLE Files
    (
        Id INT IDENTITY(1,1) NOT NULL,
        FileName NVARCHAR(255) NOT NULL,
        IsDirectory BIT NOT NULL,
        FilePath NVARCHAR(MAX) NULL,        -- changed to NULL
        UpdatedAt DATETIME DEFAULT GETDATE(),
        GUID NVARCHAR(100) NOT NULL,
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

------------------------------------------------------------
-- FOLDERS TABLE
------------------------------------------------------------
IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Folders')
BEGIN
    CREATE TABLE Folders
    (
        _Id INT IDENTITY(1,1) NOT NULL,
        name NVARCHAR(50) NOT NULL,
        isDirectory BIT NOT NULL,
        path NVARCHAR(MAX) NULL,
        parentId INT NULL,
        size BIGINT NULL,
        mimeType NVARCHAR(50) NULL,
        createdAt DATETIME DEFAULT GETDATE(),
        updatedAt DATETIME DEFAULT GETDATE(),
        GUID NVARCHAR(100) NOT NULL,
        UserId UNIQUEIDENTIFIER NOT NULL,

        CONSTRAINT PK_Folders PRIMARY KEY CLUSTERED (_Id ASC), -- FIXED
        CONSTRAINT UQ_Folders_GUID UNIQUE NONCLUSTERED (GUID ASC),
        CONSTRAINT FK_Folders_Users FOREIGN KEY (UserId) REFERENCES Users(Id)
    );

    PRINT 'Table "Folders" created.';
END
ELSE
BEGIN
    PRINT 'Table "Folders" already exists.';
END
GO
