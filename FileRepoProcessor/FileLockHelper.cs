using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace FileRepoProcessor
{
    public static class FileLockHelper
    {
        public static void Wait(string filePath, int delayMs = 500)
        {
            while (true)
            {
                try
                {
                    using var stream = File.Open(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None);

                    return; // file is free
                }
                catch (IOException)
                {
                    Thread.Sleep(delayMs);
                }
            }
        }
    }
}
