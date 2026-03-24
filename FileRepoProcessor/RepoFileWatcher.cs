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

        public RepoFileWatcher(
            IFileQueue queue,
            IFileProcessor processor,
            IOptions<PathOptions> options)
        {
            _queue = queue;
            _processor = processor;
            _paths = options.Value;
        }

        public void Start()
        {
            _watcher = new FileSystemWatcher(_paths.RepoRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += (_, e) =>
            {
                if (!_processor.IsProcessed(e.FullPath))
                    _queue.Enqueue(e.FullPath);
            };
        }
    }
}
