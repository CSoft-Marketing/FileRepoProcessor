using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class RepoFileWatcher : IFileWatcher
    {
        private readonly IFileQueue _queue;
        private readonly IFileProcessor _processor;
        private readonly PathOptions _paths;
        private FileSystemWatcher? _watcher;
        private readonly ILogger<FileProcessor> _logger;
        private readonly IPathMapper _mapper;
        public RepoFileWatcher(
            ILogger<FileProcessor> logger,
            IFileQueue queue,
            IFileProcessor processor,
            IOptions<PathOptions> options,
            IPathMapper mapper)
        {
            _logger = logger;
            _queue = queue;
            _processor = processor;
            _paths = options.Value;
            _mapper = mapper;
        }

        private static readonly HashSet<string> AllowedExtensions =
    new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx",
        ".xls", ".xlsx",
        ".ppt", ".pptx",
        ".pdf",
        ".dwg", ".dxf", ".dgn", ".ifc", ".obj", ".stl", ".stp",
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".gif"
    };
        public void Start()
        {
            _watcher = new FileSystemWatcher(_paths.RepoRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName  // | NotifyFilters.LastWrite
            };

            _watcher.Created += (_, e) =>
            {
                try
                {
                    // 🚫 Skip directories immediately
                    //if (Directory.Exists(e.FullPath))
                    //    return;
                    FileAttributes attr;
                    try
                    {
                        attr = File.GetAttributes(e.FullPath);
                    }
                    catch
                    {
                        return; // path not ready / deleted / inaccessible
                    }

                    if (attr.HasFlag(FileAttributes.Directory))
                        return;


                    // 🚫 Skip unsupported files
                    var ext = Path.GetExtension(e.FullPath);
                    if (!AllowedExtensions.Contains(ext))
                        return;
                    
                    if (!_processor.IsProcessed(e.FullPath))
                    {
                        _queue.Enqueue(new ProcessingJob
                        {
                            FilePath = e.FullPath
                        });
                    }
                }
                catch (Exception ex)
                {
                    // Never let watcher crash the service
                    _logger.LogWarning(ex, "Watcher error for {Path}", e.FullPath);
                }
            };
                //_watcher.Created += (_, e) =>
                //{
                //    if (!_processor.IsProcessed(e.FullPath))
                //        //_queue.Enqueue(e.FullPath);
                //        _queue.Enqueue(new ProcessingJob
                //        {
                //            FilePath = e.FullPath
                //        });
                //};
            }
            
    }
}
