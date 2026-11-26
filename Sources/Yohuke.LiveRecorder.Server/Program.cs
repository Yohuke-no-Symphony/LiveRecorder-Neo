using Yohuke.LiveRecorder.Server;
using Yohuke.LiveRecorder.Server.StartupFlows;

var app = ServerStartup.CreateApplication(args);
var migration = app.Services.GetRequiredService<MigrationStartupService>();
await app.RunAsync();
return migration.Failed ? 1 : 0;
