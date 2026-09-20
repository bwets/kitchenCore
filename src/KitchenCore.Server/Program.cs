using KitchenCore.Server;

// The server executable. The desktop app builds the same application in-process,
// so everything except "how it is started" lives in KitchenCoreHost.
KitchenCoreHost.Build(args).Run();
