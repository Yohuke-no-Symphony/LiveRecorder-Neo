namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

internal sealed record MigrationStep(string From, string To, Type Implementation);
