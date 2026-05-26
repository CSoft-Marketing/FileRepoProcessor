using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class RepoScanner : IRepoScanner
    {
        private readonly IFileQueue _queue;
        private readonly IFileProcessor _processor;
        private readonly PathOptions _paths;

        public RepoScanner(
            IFileQueue queue,
            IFileProcessor processor,
            IOptions<PathOptions> options)
        {
            _queue = queue;
            _processor = processor;
            _paths = options.Value;
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
        public void ScanAndEnqueue()
        {
            var files = Directory
                .EnumerateFiles(_paths.RepoRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => AllowedExtensions.Contains(Path.GetExtension(f)));

            foreach (var file in files)
            {
                if (!_processor.IsProcessed(file))
                    //_queue.Enqueue(file);
                    _queue.Enqueue(new ProcessingJob
                    {
                        FilePath = file
                    });
            }
        }
    }
}
