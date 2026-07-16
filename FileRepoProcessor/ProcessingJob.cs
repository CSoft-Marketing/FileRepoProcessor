using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public enum JobStage
    {
        Initial,
        WaitingForOC,
        FetchingOC,
        Completed,
        Failed
    }
    public class ProcessingJob
    {
        public string FilePath { get; set; }
        public string CacheFolder { get; set; }
        public string OCID { get; set; }
        public int RetryCount { get; set; }
        public JobStage Stage { get; set; } = JobStage.Initial;
    }

    public class ErrorInfo
    {
        public DateTime Time { get; set; }

        public string? File { get; set; }

        public string? OCID { get; set; }

        public int? Retries { get; set; }

        public string? Stage { get; set; }

        public DateTime? SourceLastWriteTimeUtc { get; set; }

        public string? Renderer { get; set; }

        public string? ExceptionMessage { get; set; }
    }
}
