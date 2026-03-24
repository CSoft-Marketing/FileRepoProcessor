using LoadBalancer;
using System.Threading.Channels;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var queue = Channel.CreateUnbounded<string>();

        services.AddSingleton(queue);

        services.AddHostedService<FileWatcherService>();

        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            services.AddHostedService<FileProcessorWorker>();
        }
    })
    .Build();

await host.RunAsync();