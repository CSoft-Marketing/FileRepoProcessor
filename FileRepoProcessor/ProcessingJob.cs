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
}
