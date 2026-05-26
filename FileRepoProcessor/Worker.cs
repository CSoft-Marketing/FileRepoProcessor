namespace FileRepoProcessor
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IRepoScanner _scanner;
        private readonly IFileWatcher _watcher;
        private readonly IFileQueue _queue;
        private readonly IFileProcessor _processor;

        public Worker(
            ILogger<Worker> logger,
            IRepoScanner scanner,
            IFileWatcher watcher,
            IFileQueue queue,
            IFileProcessor processor)
        {
            _logger = logger;
            _scanner = scanner;
            _watcher = watcher;
            _queue = queue;
            _processor = processor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker started");
            _scanner.ScanAndEnqueue();
            _watcher.Start();

            while (!stoppingToken.IsCancellationRequested)
            {
                //if (_queue.TryDequeue(out var file))
                //{
                //    _logger.LogInformation("Processing file {File}", file);
                //    await _processor.ProcessAsync(file, stoppingToken);
                //}
                if (_queue.TryDequeue(out var job))
                {
                    await _processor.ProcessAsync(job, stoppingToken);
                }
                else
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            _logger.LogInformation("Worker stopping");
        }
        
    }
}
