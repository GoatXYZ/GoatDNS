using GoatDNS.Core.Logging;
using GoatDNS.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows Service in production; as a console app when launched directly (debugging).
builder.Services.AddWindowsService(options => options.ServiceName = "GoatDNS");

builder.Services.AddSingleton(new QueryLog());
builder.Services.AddSingleton(sp => new RuntimeState(sp.GetRequiredService<QueryLog>()));
builder.Services.AddHostedService<GoatDnsWorker>();

var host = builder.Build();
host.Run();
