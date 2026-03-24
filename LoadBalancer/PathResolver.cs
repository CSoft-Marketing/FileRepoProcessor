using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoadBalancer
{
    public static class PathResolver
    {
        private const string RepoRoot = @"C:\Users\sn\Repo1";
        private const string CacheRoot = @"C:\Users\sn\Cache1";

        public static string GetOutputFolder(string inputFile)
        {
            var relativePath = Path.GetRelativePath(RepoRoot, inputFile);

            var directory = Path.GetDirectoryName(relativePath);

            var fileName = Path.GetFileNameWithoutExtension(inputFile);
            var ext = Path.GetExtension(inputFile).TrimStart('.');

            var folderName = $"{fileName}_{ext}";

            return Path.Combine(CacheRoot, directory ?? "", folderName);
        }
    }
}
