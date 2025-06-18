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

-- Drop Files table if it exists (must drop this first due to FK to Users)
IF OBJECT_ID('Files', 'U') IS NOT NULL
BEGIN
    DROP TABLE Files;
    PRINT 'Table "Files" dropped.';
END
GO

-- Drop Users table if it exists
IF OBJECT_ID('Users', 'U') IS NOT NULL
BEGIN
    DROP TABLE Users;
    PRINT 'Table "Users" dropped.';
END
GO

-- Recreate Users table
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
GO

-- Recreate Files table with FK to Users
CREATE TABLE Files (
    Id INT IDENTITY(1,1) NOT NULL,
    FileName NVARCHAR(50) NOT NULL,
    GUID NVARCHAR(50) NOT NULL,
    UserId UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT PK_Files PRIMARY KEY CLUSTERED (Id ASC),
    CONSTRAINT UQ_Files_GUID UNIQUE NONCLUSTERED (GUID ASC),
    CONSTRAINT FK_Files_Users FOREIGN KEY (UserId) REFERENCES Users(Id)
);
PRINT 'Table "Files" created with constraints.';
GO
