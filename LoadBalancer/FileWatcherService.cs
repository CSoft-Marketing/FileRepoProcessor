using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LoadBalancer
{
    public class FileWatcherService : BackgroundService
    {
        private readonly Channel<string> _queue;
        private FileSystemWatcher _watcher;

        private const string RepoRoot = @"C:\Users\sn\Repo1";

        public FileWatcherService(Channel<string> queue)
        {
            _queue = queue;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _watcher = new FileSystemWatcher(RepoRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += async (s, e) =>
            {
                if (File.Exists(e.FullPath))
                {
                    await _queue.Writer.WriteAsync(e.FullPath);
                }
            };

            return Task.CompletedTask;
        }
    }
}
