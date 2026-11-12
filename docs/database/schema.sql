IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [AlertRules] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(200) NOT NULL,
        [RuleType] int NOT NULL,
        [Threshold] float NOT NULL,
        [WindowMinutes] int NOT NULL,
        [Severity] int NOT NULL,
        [Channels] int NOT NULL,
        [Recipients] nvarchar(1000) NULL,
        [CooldownMinutes] int NOT NULL,
        [Enabled] bit NOT NULL,
        CONSTRAINT [PK_AlertRules] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiModules] (
        [Id] int NOT NULL IDENTITY,
        [ModuleName] nvarchar(100) NOT NULL,
        [DisplayName] nvarchar(200) NOT NULL,
        [Icon] nvarchar(60) NULL,
        [Description] nvarchar(max) NULL,
        [ScreenName] nvarchar(200) NULL,
        [ShowInNav] bit NOT NULL,
        [SortOrder] int NOT NULL,
        CONSTRAINT [PK_ApiModules] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] int NOT NULL IDENTITY,
        [TimestampUtc] datetime2 NOT NULL,
        [UserName] nvarchar(200) NOT NULL,
        [Category] nvarchar(50) NOT NULL,
        [Action] nvarchar(200) NOT NULL,
        [Target] nvarchar(400) NULL,
        [Details] nvarchar(4000) NULL,
        [IpAddress] nvarchar(64) NULL,
        [Success] bit NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [DashboardCaches] (
        [Id] int NOT NULL IDENTITY,
        [Key] nvarchar(200) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL,
        [ExpiresUtc] datetime2 NULL,
        CONSTRAINT [PK_DashboardCaches] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [IncidentActions] (
        [Id] int NOT NULL IDENTITY,
        [IncidentKey] nvarchar(100) NOT NULL,
        [ActionType] int NOT NULL,
        [UserName] nvarchar(200) NOT NULL,
        [Value] nvarchar(2000) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_IncidentActions] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [ReportSchedules] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(200) NOT NULL,
        [ReportType] int NOT NULL,
        [Frequency] int NOT NULL,
        [Format] int NOT NULL,
        [Recipients] nvarchar(1000) NOT NULL,
        [NextRunUtc] datetime2 NOT NULL,
        [LastRunUtc] datetime2 NULL,
        [LastStatus] nvarchar(max) NULL,
        [Enabled] bit NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        CONSTRAINT [PK_ReportSchedules] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [Roles] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(50) NOT NULL,
        [Description] nvarchar(max) NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [AlertHistories] (
        [Id] int NOT NULL IDENTITY,
        [RuleId] int NULL,
        [RuleType] int NOT NULL,
        [Severity] int NOT NULL,
        [Title] nvarchar(300) NOT NULL,
        [Message] nvarchar(max) NOT NULL,
        [SourceKey] nvarchar(200) NULL,
        [RaisedUtc] datetime2 NOT NULL,
        [AcknowledgedBy] nvarchar(max) NULL,
        [AcknowledgedUtc] datetime2 NULL,
        [NotificationStatus] nvarchar(max) NULL,
        CONSTRAINT [PK_AlertHistories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AlertHistories_AlertRules_RuleId] FOREIGN KEY ([RuleId]) REFERENCES [AlertRules] ([Id]) ON DELETE SET NULL
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiEndpoints] (
        [Id] int NOT NULL IDENTITY,
        [ModuleId] int NOT NULL,
        [EndpointName] nvarchar(500) NOT NULL,
        [HttpMethod] nvarchar(10) NOT NULL,
        [RelativeUrl] nvarchar(450) NOT NULL,
        [Description] nvarchar(max) NULL,
        [RequestSchema] nvarchar(max) NULL,
        [ResponseSchema] nvarchar(max) NULL,
        [Kind] int NOT NULL,
        [Enabled] bit NOT NULL,
        [IsSensitive] bit NOT NULL,
        CONSTRAINT [PK_ApiEndpoints] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ApiEndpoints_ApiModules_ModuleId] FOREIGN KEY ([ModuleId]) REFERENCES [ApiModules] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [Username] nvarchar(200) NOT NULL,
        [DisplayName] nvarchar(200) NULL,
        [Email] nvarchar(256) NULL,
        [RoleId] int NOT NULL,
        [IsActive] bit NOT NULL,
        [Theme] nvarchar(20) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        [LastLoginUtc] datetime2 NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Users_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiMappings] (
        [Id] int NOT NULL IDENTITY,
        [ApiEndpointId] int NOT NULL,
        [ScreenName] nvarchar(200) NOT NULL,
        [WidgetType] int NOT NULL,
        [RefreshInterval] int NOT NULL,
        [ExecutionMode] int NOT NULL,
        CONSTRAINT [PK_ApiMappings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ApiMappings_ApiEndpoints_ApiEndpointId] FOREIGN KEY ([ApiEndpointId]) REFERENCES [ApiEndpoints] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [FavoriteAtms] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [AtmId] nvarchar(100) NOT NULL,
        [AtmName] nvarchar(max) NULL,
        [CreatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_FavoriteAtms] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_FavoriteAtms_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [SavedSearches] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [SearchType] nvarchar(50) NOT NULL,
        [QueryJson] nvarchar(max) NOT NULL,
        [CreatedUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_SavedSearches] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_SavedSearches_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE TABLE [ApiFieldMappings] (
        [Id] int NOT NULL IDENTITY,
        [ApiMappingId] int NOT NULL,
        [FieldPath] nvarchar(300) NOT NULL,
        [Label] nvarchar(200) NOT NULL,
        [WidgetType] int NOT NULL,
        [Aggregation] nvarchar(20) NULL,
        CONSTRAINT [PK_ApiFieldMappings] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ApiFieldMappings_ApiMappings_ApiMappingId] FOREIGN KEY ([ApiMappingId]) REFERENCES [ApiMappings] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AlertHistories_RaisedUtc] ON [AlertHistories] ([RaisedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AlertHistories_RuleId] ON [AlertHistories] ([RuleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AlertHistories_RuleType_SourceKey_RaisedUtc] ON [AlertHistories] ([RuleType], [SourceKey], [RaisedUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApiEndpoints_HttpMethod_RelativeUrl] ON [ApiEndpoints] ([HttpMethod], [RelativeUrl]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApiEndpoints_ModuleId] ON [ApiEndpoints] ([ModuleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApiFieldMappings_ApiMappingId] ON [ApiFieldMappings] ([ApiMappingId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ApiMappings_ApiEndpointId] ON [ApiMappings] ([ApiEndpointId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ApiModules_ModuleName] ON [ApiModules] ([ModuleName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Category_TimestampUtc] ON [AuditLogs] ([Category], [TimestampUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_TimestampUtc] ON [AuditLogs] ([TimestampUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_UserName] ON [AuditLogs] ([UserName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_DashboardCaches_Key] ON [DashboardCaches] ([Key]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_FavoriteAtms_UserId_AtmId] ON [FavoriteAtms] ([UserId], [AtmId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_IncidentActions_IncidentKey] ON [IncidentActions] ([IncidentKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Roles_Name] ON [Roles] ([Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SavedSearches_UserId] ON [SavedSearches] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Users_RoleId] ON [Users] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Username] ON [Users] ([Username]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112121018_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261112121018_InitialCreate', N'8.0.11');
END;
GO

COMMIT;
GO

