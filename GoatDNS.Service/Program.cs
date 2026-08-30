using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;
using GoatDNS.Service;
using GoatDNS.WinDivert;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows Service in production; as a console app when launched directly (debugging).
builder.Services.AddWindowsService(options => options.ServiceName = "GoatDNS");

builder.Services.AddSingleton<QueryLog>();
builder.Services.AddSingleton(sp =>
{
    var log = sp.GetRequiredService<QueryLog>();
    return new GoatDnsHost(log, WinDivertCaptureProvider.Factory(log));
});
builder.Services.AddHostedService<GoatDnsWorker>();

var host = builder.Build();
host.Run();
