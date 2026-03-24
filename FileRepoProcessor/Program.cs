using FileRepoProcessor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// Create log directory
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

IHost host = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext())
    .ConfigureServices((context, services) =>
    {
        services.Configure<PathOptions>(
            context.Configuration.GetSection("Paths"));

        services.AddSingleton<IFileQueue, FileQueue>();
        services.AddSingleton<IPathMapper, PathMapper>();
        services.AddSingleton<IRepoScanner, RepoScanner>();
        services.AddSingleton<IFileWatcher, RepoFileWatcher>();
        services.AddSingleton<IFileProcessor, FileProcessor>();

        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();