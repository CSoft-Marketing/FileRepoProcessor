using Microsoft.Extensions.Options;
using Spire.Pdf.Graphics;
using System.Drawing;

namespace FileRepoProcessor
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IRepoScanner _scanner;
        private readonly IFileWatcher _watcher;
        private readonly IFileQueue _queue;
        private readonly IFileProcessor _processor;
        private readonly BrowserInfo _browserInfo;
        //private readonly PathOptions _paths;
        private readonly PathOptions _paths;
        private readonly IHtmlToPdfConverter _htmlToPdfConverter;
        
        public Worker(
            ILogger<Worker> logger,
            IRepoScanner scanner,
            IFileWatcher watcher,
            IFileQueue queue,
            IFileProcessor processor,
            BrowserInfo browserInfo,
            //PathOptions paths,
            IOptions<PathOptions> options,
        IHtmlToPdfConverter htmlToPdfConverter)
        {
            _logger = logger;
            _scanner = scanner;
            _watcher = watcher;
            _queue = queue;
            _processor = processor;
            _browserInfo = browserInfo;
            _htmlToPdfConverter = htmlToPdfConverter;
            _paths = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker started");
            
            if (_browserInfo.HasChromiumBrowser)
            {
                try
                {
                    if(!_paths.UseChromium)
                        throw new NotSupportedException(
                            "Chromium rendering has been disabled by configuration.");
                    _browserInfo.isUsable = _htmlToPdfConverter.ValidateChrome();
                    _logger.LogInformation("Chrome renderer validated successfully.");
                }
                catch (Exception ex)
                {
                    _browserInfo.isUsable = false;
                    //_rendererState.ValidationError = ex.Message;

                    _logger.LogInformation(ex,
                        "Chrome renderer validation failed. Qt renderer will be used.");
                }
            }
            else
            {
                _browserInfo.isUsable = false;

                _logger.LogInformation("No Chromium browser found. Qt renderer will be used.");
            }
            if (_browserInfo.HasChromiumBrowser && _browserInfo.isUsable)
            {
                _logger.LogInformation("======================================");
                _logger.LogInformation("HTML Rendering Engine Detection");
                _logger.LogInformation("======================================");
                _logger.LogInformation("Browser     : {Browser}", _browserInfo.Type);
                _logger.LogInformation("Version     : {Version}", _browserInfo.Version);
                _logger.LogInformation("Executable  : {Path}", _browserInfo.ExecutablePath);
            }
            else
            {
                _logger.LogInformation("Chromium browser unavailable or restricted.");
                _logger.LogInformation("Qt renderer will be used.");
            }

            _logger.LogInformation("======================================");
            

            _scanner.ScanAndEnqueue();
            _watcher.Start();

            while (!stoppingToken.IsCancellationRequested)
            {
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
