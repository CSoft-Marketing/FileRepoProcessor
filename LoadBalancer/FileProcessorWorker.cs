using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LoadBalancer
{
    public class FileProcessorWorker : BackgroundService
    {
        private readonly Channel<string> _queue;

        public FileProcessorWorker(Channel<string> queue)
        {
            _queue = queue;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var file in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    ProcessFile(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {file}: {ex.Message}");
                }
            }
        }

        private async Task ProcessFile(string inputPath)
        {
            if (!File.Exists(inputPath))
                return;

            if (!IsFileReady(inputPath))
                return;

            var outputFolder = PathResolver.GetOutputFolder(inputPath);

            Directory.CreateDirectory(outputFolder);

            Console.WriteLine($"Processing {inputPath}");

            // TODO: conversion logic here
            await Task.Delay(2000);

            File.WriteAllText(Path.Combine(outputFolder, "_c"), "complete");

            Console.WriteLine($"Completed {inputPath}");
        }

        //private void ProcessFile(string inputFile)
        //{
        //    var outputFolder = PathResolver.GetOutputFolder(inputFile);

        //    Directory.CreateDirectory(outputFolder);

        //    // your conversion logic here

        //    File.WriteAllText(Path.Combine(outputFolder, "_c"), "complete");
        //}

        private bool IsFileReady(string path)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
