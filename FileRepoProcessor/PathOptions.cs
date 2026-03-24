using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class PathOptions
    {
        public string RepoRoot { get; set; } = "";
        public string CacheRoot { get; set; } = "";
        public string OCUrl { get; set; } = "";
        public string RasterJob { get; set; } = "";
        public string TextJob { get; set; } = "";
        public string XDataJob { get; set; } = "";
        public string TokenNumber { get; set; } = "";
    
    }

    

    public class FileQueue : IFileQueue
    {
        private readonly ConcurrentQueue<string> _queue = new();

        public void Enqueue(string path) => _queue.Enqueue(path);
        public bool TryDequeue(out string path) => _queue.TryDequeue(out path);
    }

    public class Indexdetails
    {
        public int page;
        public string Width;
        public string Height;
        public int angle;

        public Indexdetails()
        {
            this.page = 0;
            this.Width = string.Empty;
            this.Height = string.Empty;
            this.angle = 0;
        }
    }
}
