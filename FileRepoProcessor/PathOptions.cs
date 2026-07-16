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
        public bool UseChromium { get; set; } = true;


    }

    

    public class FileQueue : IFileQueue
    {
        //private readonly ConcurrentQueue<string> _queue = new();

        //public void Enqueue(string path) => _queue.Enqueue(path);
        //public bool TryDequeue(out string path) => _queue.TryDequeue(out path);
        /*
        private readonly ConcurrentQueue<ProcessingJob> _queue = new();

        public void Enqueue(ProcessingJob job)
        {
            _queue.Enqueue(job);
        }

        public bool TryDequeue(out ProcessingJob job)
        {
            return _queue.TryDequeue(out job);
        }*/
        private readonly ConcurrentQueue<ProcessingJob> _queue = new();
        private readonly ConcurrentDictionary<string, byte> _inQueue = new();

        //public void Enqueue(ProcessingJob job)
        //{
        //    if (_inQueue.TryAdd(job.FilePath, 0))
        //    {
        //        _queue.Enqueue(job);
        //    }
        //}
        public void Enqueue(ProcessingJob job)
        {
            var key = $"{job.FilePath}_{job.Stage}";

            if (_inQueue.TryAdd(key, 0))
            {
                _queue.Enqueue(job);
            }
        }
        //public bool TryDequeue(out ProcessingJob job)
        //{
        //    if (_queue.TryDequeue(out job))
        //    {
        //        _inQueue.TryRemove(job.FilePath, out _);
        //        return true;
        //    }
        //    return false;
        //}
        public bool TryDequeue(out ProcessingJob job)
        {
            if (_queue.TryDequeue(out job))
            {
                var key = $"{job.FilePath}_{job.Stage}";
                _inQueue.TryRemove(key, out _);
                return true;
            }
            return false;
        }
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

    public class DimDetails
    {
        public int width { get; set; }
        public int height { get; set; }
        

        public DimDetails()
        {
            this.width = 0;
            this.height = 0;
            
        }
        public DimDetails(int W, int H)
        {
            this.width = W;
            this.height = H;
            
        }

    }
}
