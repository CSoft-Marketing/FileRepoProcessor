using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class PathMapper : IPathMapper
    {
        private readonly PathOptions _paths;

        public PathMapper(IOptions<PathOptions> options)
        {
            _paths = options.Value;
        }

        public string GetCacheFolder(string repoFile)
        {
            var relative = Path.GetRelativePath(_paths.RepoRoot, repoFile);

            var dir = Path.GetDirectoryName(relative)!;
            var name = Path.GetFileNameWithoutExtension(relative);
            var ext = Path.GetExtension(relative).TrimStart('.');

            return Path.Combine(_paths.CacheRoot, dir, $"{name}_{ext}");
        }

        public string GetOCUrl()
        {
            return _paths.OCUrl;
        }

    }
}
