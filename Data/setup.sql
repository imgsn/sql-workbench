-- Run manually against a dedicated workbench application database.
-- Never run this script against a database being compared.
IF SCHEMA_ID(N'workbench') IS NULL EXEC(N'CREATE SCHEMA [workbench]');
GO
IF OBJECT_ID(N'workbench.ComparisonProfiles', N'U') IS NULL
BEGIN
    CREATE TABLE [workbench].[ComparisonProfiles] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ComparisonProfiles] PRIMARY KEY,
        [Name] nvarchar(100) NOT NULL,
        [Mode] nvarchar(20) NOT NULL,
        [SourceSchema] nvarchar(128) NULL,
        [TargetSchema] nvarchar(128) NULL,
        [CreatedAt] datetimeoffset NOT NULL
    );
    CREATE UNIQUE INDEX [IX_ComparisonProfiles_Name] ON [workbench].[ComparisonProfiles]([Name]);
END;
GO
