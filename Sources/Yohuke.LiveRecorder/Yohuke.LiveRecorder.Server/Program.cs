using Yohuke.LiveRecorder.Server;

var app = ServerStartup.CreateApplication(args);
await app.RunAsync();
