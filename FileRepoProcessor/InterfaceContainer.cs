using Spire.Additions.Qt;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public interface IPathMapper
    {
        string GetCacheFolder(string repoFilePath);
        string GetOCUrl();
    }
    
    public interface IRepoScanner
    {
        void ScanAndEnqueue();
    }
    public interface IFileWatcher
    {
        void Start();
    }
    public interface IFileProcessor
    {
        bool IsProcessed(string repoFilePath);
        //Task ProcessAsync(string repoFilePath, CancellationToken token);
        Task ProcessAsync(ProcessingJob job, CancellationToken token);
    }
    public interface IFileQueue
    {
        //void Enqueue(string path);
        void Enqueue(ProcessingJob job);
        //bool TryDequeue(out string path);
        bool TryDequeue(out ProcessingJob job);
    }

    public interface IHtmlToPdfConverter
    {
        Stream Convert(HtmlToPdfRequest request);
        bool ValidateChrome();
    }
}
