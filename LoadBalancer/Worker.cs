namespace LoadBalancer
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private const string RepoRoot = @"C:\Users\sn\Repo1";
        private const string CacheRoot = @"C:\Users\sn\Cache1";
        private const string ProcessingRoot = @"C:\Users\sn\Processing";
        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        //protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        //{
        //    while (!stoppingToken.IsCancellationRequested)
        //    {
        //        _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
        //        await Task.Delay(1000, stoppingToken);
        //    }
        //}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var files = Directory.GetFiles(RepoRoot, "*.*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    TryClaimFile(file);
                }

                await Task.Delay(2000, stoppingToken);
            }
        }

        private void TryClaimFile(string file)
        {
            try
            {
                var relative = Path.GetRelativePath(RepoRoot, file);

                var processingPath = Path.Combine(ProcessingRoot, relative);

                var directory = Path.GetDirectoryName(relative);

                var fileName = Path.GetFileNameWithoutExtension(file);
                var ext = Path.GetExtension(file).TrimStart('.');

                var completionFile = Path.Combine(CacheRoot, directory ?? "", $"{fileName}_{ext}","_c");

                if (!File.Exists(completionFile))
                {

                    Directory.CreateDirectory(Path.GetDirectoryName(processingPath));

                    File.Move(file, processingPath);

                    Task.Run(() => ProcessFile(processingPath));
                }
            }
            catch
            {
                // Another worker already claimed it
            }
        }

        private void ProcessFile(string inputFile)
        {
            try
            {
                var relative = Path.GetRelativePath(ProcessingRoot, inputFile);

                var directory = Path.GetDirectoryName(relative);

                var fileName = Path.GetFileNameWithoutExtension(inputFile);
                var ext = Path.GetExtension(inputFile).TrimStart('.');

                var outputFolder = Path.Combine(CacheRoot, directory ?? "", $"{fileName}_{ext}");

                Directory.CreateDirectory(outputFolder);

                Console.WriteLine($"Processing {inputFile}");

                // Simulate processing
                Thread.Sleep(2000);

                // Completion marker
                File.WriteAllText(Path.Combine(outputFolder, "_c"), "complete");

                Console.WriteLine($"Completed {inputFile}");

                File.Delete(inputFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Processing error: {ex.Message}");
            }
        }
    }
}

