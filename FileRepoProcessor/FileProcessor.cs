using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using HarfBuzzSharp;
using ImageMagick;
using ImageMagick.Drawing;
using ImageMagick.Formats;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog.Events;
using SkiaSharp;
using Spire.Additions.Qt;

//using Spire.Additions.Xps.Schema;
using Spire.Doc;
using Spire.Doc.Interface;
using Spire.Pdf;
using Spire.Pdf.Graphics;
using Spire.Presentation;
using Spire.Presentation.AI;
using Spire.Xls;
using Spire.Xls.Core;
using static System.Net.Mime.MediaTypeNames;

namespace FileRepoProcessor
{
    public class FileProcessor : IFileProcessor
    {
        private readonly IPathMapper _mapper;
        private readonly ILogger<FileProcessor> _logger;
        private readonly PathOptions _paths;
        private readonly IFileQueue _queue;
        private readonly BrowserInfo _browserInfo;
        private readonly IHtmlToPdfConverter _htmlToPdfConverter;
        
        public FileProcessor(ILogger<FileProcessor> logger, IPathMapper mapper, IOptions<PathOptions> options, IFileQueue queue, IHtmlToPdfConverter htmlToPdfConverter, BrowserInfo browserInfo)
        {
            _mapper = mapper;
            _logger = logger;
            _paths = options.Value;
            _queue = queue;
            _htmlToPdfConverter = htmlToPdfConverter;
            _browserInfo = browserInfo;
        }
        public bool IsProcessed(string repoFile)
        {
            var cacheFolder = _mapper.GetCacheFolder(repoFile);
            var encryptFile = System.IO.Path.Combine(cacheFolder, "_e");
            if (File.Exists(encryptFile))
                return true;
            var errFile = System.IO.Path.Combine(cacheFolder, "_err");
            if (File.Exists(errFile))
            {
                var errorInfo = JsonConvert.DeserializeObject<ErrorInfo>( File.ReadAllText(errFile));
                DateTime currentTime = File.GetLastWriteTimeUtc(repoFile);
                if (errorInfo!=null && currentTime == errorInfo.SourceLastWriteTimeUtc)
                    return true;
                // File has changed since the error.
                // Remove the stale error marker.
                File.Delete(errFile);
            }
            return File.Exists(System.IO.Path.Combine(cacheFolder, "_c")) ||
                   File.Exists(System.IO.Path.Combine(cacheFolder, "_p"));
                   
        }
        public async Task ProcessAsync(ProcessingJob job, CancellationToken token)
        {
            switch (job.Stage)
            {
                case JobStage.Initial:
                    await HandleInitial(job);
                    break;

                case JobStage.WaitingForOC:
                    await HandleWaiting(job);
                    break;

                case JobStage.FetchingOC:
                    await HandleFetch(job);
                    break;
            }
        }
        public static bool IsCAD(string ext)
        {
            if (ext.ToLower().Equals(".dwg") || ext.ToLower().Equals(".dxf") ||
                ext.ToLower().Equals(".dgn") || ext.ToLower().Equals(".ifc") ||
                ext.ToLower().Equals(".stl") || ext.ToLower().Equals(".obj") ||
                ext.ToLower().Equals(".stp"))
                return true;
            return false;
        }
        public static bool IsFetchableFile(string ext)
        {
            if (ext.ToLower().Equals(".dwg") || ext.ToLower().Equals(".dxf"))
                return true;
            return false;
        }
        

        private async Task HandleInitial(ProcessingJob job)
        {
            FileLockHelper.Wait(job.FilePath);

            job.CacheFolder = _mapper.GetCacheFolder(job.FilePath);
            Directory.CreateDirectory(job.CacheFolder);

            if (!TryCreateProcessingMarker(job.CacheFolder))
            {
                _logger.LogInformation($"Skipping {job.FilePath}, already under process.");
                return;
            }

            bool success = false;

            try
            {
                string ext = System.IO.Path.GetExtension(job.FilePath).ToLower();

                if (IsCAD(ext))
                {
                    job.OCID = await StartOCWithXRefs(job);

                    if (IsFetchableFile(ext))
                    {
                        job.Stage = JobStage.WaitingForOC;
                        job.RetryCount = 0;
                        _queue.Enqueue(job);

                        success = true; // ⚠️ important: accepted successfully
                        return;
                    }

                    _logger.LogInformation($"Non-fetchable CAD processed via OC: {job.FilePath}");
                    success = true;
                    return;
                }

                // Non-CAD
                ProcessNonCAD(job.FilePath, job.CacheFolder);
                success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing {job.FilePath}");
                MarkError(job); // 🔥 mark failure
                //throw; // optional depending on your queue strategy
                //throw will force the system to stop which is undesirable
            }
            finally
            {
                if (success)
                {
                    MarkCompleted(job);
                }
                else
                {
                    // ensure _p is removed even if MarkError didn't
                    CleanupProcessingMarker(job);
                }
            }
        }

        private void CleanupProcessingMarker(ProcessingJob job)
        {
            string pFile = System.IO.Path.Combine(job.CacheFolder, "_p");

            if (File.Exists(pFile))
                File.Delete(pFile);
        }
        private void CreateProcessingMarker(string cacheFolder)
        {
            string pFile = System.IO.Path.Combine(cacheFolder, "_p");

            if (!File.Exists(pFile))
            {
                File.WriteAllText(pFile, DateTime.UtcNow.ToString("o"));
            }
        }

        private bool TryCreateProcessingMarker(string cacheFolder)
        {
            string pFile = System.IO.Path.Combine(cacheFolder, "_p");

            try
            {
                using (var fs = new FileStream(
                    pFile,
                    FileMode.CreateNew,   // 🔥 atomic creation
                    FileAccess.Write,
                    FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.WriteLine(DateTime.UtcNow.ToString("o"));
                    writer.WriteLine(Environment.MachineName);
                    writer.WriteLine("WORKER");
                }

                return true; // lock acquired
            }
            catch (IOException)
            {
                return false; // already exists → someone else owns it
            }
        }
        private async Task HandleWaiting(ProcessingJob job)
        {
            bool ready = await IsOCReady(job); // implement simple status check

            if (!ready)
            {
                job.RetryCount++;

                if (job.RetryCount > 50)
                {
                    _logger.LogWarning("OC timeout: {File}", job.FilePath);
                    job.Stage = JobStage.Failed;
                    return;
                }

                await Task.Delay(3000);
                _queue.Enqueue(job);
                return;
            }

            job.Stage = JobStage.FetchingOC;
            _queue.Enqueue(job);
        }
        private async Task<bool> IsOCReady(ProcessingJob job)
        {
            try
            {
                var text = await getOCFiles(job.OCID, _paths.TokenNumber, "textdata");
                var xdata = await getOCFiles(job.OCID, _paths.TokenNumber, "xdata");
                responseObj obj = await getOCFilesBinary(job.OCID, _paths.TokenNumber, "thumbnail.bmp");
                return (!string.IsNullOrEmpty(text) && !text.StartsWith("S")) &&
                       (!string.IsNullOrEmpty(xdata) && !xdata.StartsWith("S") && obj.content!= null);
            }
            catch
            {
                return false;
            }
        }
        private async Task HandleFetch(ProcessingJob job)
        {
            try
            {
                var success = await FetchOCFiles(job);

                if (success)
                {
                    MarkCompleted(job);
                    return;
                }
                const int MAX_RETRIES = 10;
                // ✅ Expected failure (OC not ready / partial fetch)
                job.RetryCount++;

                if (job.RetryCount < MAX_RETRIES)
                {
                    int delay = Math.Min(16000, (int)Math.Pow(2, job.RetryCount) * 1000);

                    _logger.LogWarning($"Retry {job.RetryCount} after {delay}ms for {job.FilePath}");

                    await Task.Delay(delay);
                    _queue.Enqueue(job);
                }
                else
                {
                    _logger.LogError($"Fetch failed after {MAX_RETRIES} retries: {job.FilePath}");
                    MarkError(job);
                }
                
            }
            catch (Exception ex)
            {
                // ❗ Unexpected failure (network crash, bug, etc.)
                job.RetryCount++;

                if (job.RetryCount < 8)
                {
                    _logger.LogWarning(ex, $"Exception retry {job.RetryCount} for {job.FilePath}");
                    await Task.Delay(2000);
                    _queue.Enqueue(job);
                }
                else
                {
                    _logger.LogError(ex, $"Fatal fetch failure: {job.FilePath}");
                    MarkError(job); // 🔥 IMPORTANT
                }
            }
        }
        private void ProcessNonCAD(string filePath, string cacheFolder)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();
                if (SupportedExtensions.Word.Contains(ext))
                {
                    ProcessWord(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Excel.Contains(ext))
                {
                    ProcessExcel(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Powerpoint.Contains(ext))
                {
                    ProcessPowerPoint(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Pdf.Contains(ext))
                {
                    ProcessPDF(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Raster.Contains(ext))
                {
                    ProcessRaster(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Email.Contains(ext))
                {
                    ProcessEmail(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Html.Contains(ext))
                {
                    ProcessHtml(filePath, cacheFolder);
                }
                else if (SupportedExtensions.Text.Contains(ext))
                {
                    ProcessText(filePath, cacheFolder);
                }
                
            }
            catch (Exception ex)
            {
                throw (ex as Exception);
            }
        }
        private void MarkCompleted(ProcessingJob job)
        {
            string pFile = System.IO.Path.Combine(job.CacheFolder, "_p");
            string cFile = System.IO.Path.Combine(job.CacheFolder, "_c");
            string xFile = System.IO.Path.Combine(job.CacheFolder, "_x.txt");
            string eFile = System.IO.Path.Combine(job.CacheFolder, "_err");
            if (File.Exists(pFile))
                File.Delete(pFile);
            if (File.Exists(xFile))
                File.Delete(xFile);
            if (File.Exists(eFile))
                File.Delete(eFile);
            File.WriteAllText(cFile, DateTime.UtcNow.ToString("o"));
        }
        //private void MarkError(ProcessingJob job)
        //{
        //    string pFile = System.IO.Path.Combine(job.CacheFolder, "_p");
        //    string eFile = System.IO.Path.Combine(job.CacheFolder, "_err");

        //    try
        //    {
        //        if (File.Exists(pFile))
        //            File.Delete(pFile);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogWarning(ex, $"Failed to delete _p for {job.FilePath}");
        //    }

        //    var errorInfo = new
        //    {
        //        Time = DateTime.UtcNow,
        //        File = job.FilePath,
        //        OCID = job.OCID,
        //        Retries = job.RetryCount,
        //        Stage = job.Stage.ToString()
        //    };

        //    File.WriteAllText(eFile, JsonConvert.SerializeObject(errorInfo));

        //    _logger.LogError($"Marked error for {job.FilePath}");
        //}
        

        private void MarkError(ProcessingJob job)
        {
            string pFile = System.IO.Path.Combine(job.CacheFolder, "_p");
            string eFile = System.IO.Path.Combine(job.CacheFolder, "_err");
            string cFile = System.IO.Path.Combine(job.CacheFolder, "_c");

            // ❗ Don't overwrite success
            if (File.Exists(cFile))
                File.Delete(cFile);

            try
            {
                if (File.Exists(pFile))
                    File.Delete(pFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to delete _p for {job.FilePath}");
            }

            var errorInfo = new ErrorInfo
            {
                Time = DateTime.UtcNow,
                File = job.FilePath,
                OCID = job.OCID,
                Retries = job.RetryCount,
                Stage = job.Stage.ToString(),
                SourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(job.FilePath)
            };

            File.WriteAllText(eFile, JsonConvert.SerializeObject(errorInfo, Newtonsoft.Json.Formatting.Indented));

            _logger.LogError($"Marked error for {job.FilePath}");
        }
        private void ProcessWord(string filepath, string cacheFolder)
        {
            try
            {
                Spire.Doc.Document doc = new Spire.Doc.Document();
                doc.LoadFromFile(filepath);
                // Optional but recommended
                doc.UpdateTableOfContents();
                ToPdfParameterList pdfParams = new ToPdfParameterList();
                pdfParams.PreserveFormFields = true;
                // Convert to PDF stream
                using var pdfStream = new MemoryStream();
                doc.SaveToStream(pdfStream, Spire.Doc.FileFormat.PDF);
                _logger.LogInfo("Saved portable version in stream");
                // Rewind before reading
                pdfStream.Position = 0;
                using var pdfDocument = new Spire.Pdf.PdfDocument(pdfStream);
                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                // Rewind again because PdfDocument may have advanced the stream
                pdfStream.Position = 0;
                // XOR the PDF stream
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw (ex);
            }
        }
        public static byte[] ResizeImage(Stream imageStream, int newWidth, int newHeight)
        {
            try
            {
                // Ensure stream is readable from start
                if (imageStream.CanSeek)
                    imageStream.Position = 0;

                using var original = SKBitmap.Decode(imageStream);

                // CASE 1: Blank worksheet → create white image
                if (original == null)
                {
                    return CreateBlankThumbnail(newWidth, newHeight);
                }

                // CASE 2: Normal image
                using var resized = original.Resize(
                    new SKImageInfo(newWidth, newHeight),
                    SKFilterQuality.High);

                // Safety: Resize can also fail
                if (resized == null)
                {
                    return CreateBlankThumbnail(newWidth, newHeight);
                }

                using var skImage = SKImage.FromBitmap(resized);
                using var outputStream = new MemoryStream();

                skImage.Encode(SKEncodedImageFormat.Png, 100)
                       .SaveTo(outputStream);

                return outputStream.ToArray();
            }
            catch
            {
                throw;
            }
        }
        private static byte[] CreateBlankThumbnail(int width, int height)
        {
            try
            {
                using var bitmap = new SKBitmap(width, height);
                using var canvas = new SKCanvas(bitmap);

                canvas.Clear(SKColors.White);

                using var image = SKImage.FromBitmap(bitmap);
                using var ms = new MemoryStream();

                image.Encode(SKEncodedImageFormat.Png, 100)
                     .SaveTo(ms);

                return ms.ToArray();
            }
            catch
            {
                throw;
            }
        }
        public static void PrepareWorksheetForPdf(Worksheet worksheet)
        {
            if (worksheet == null) return;

            worksheet.PageSetup.LeftHeader = "";
            worksheet.PageSetup.CenterHeader = "";
            worksheet.PageSetup.RightHeader = "";

            worksheet.PageSetup.LeftFooter = "";
            worksheet.PageSetup.CenterFooter = "";
            worksheet.PageSetup.RightFooter = "";

            int maxRow = worksheet.LastRow;
            int maxCol = worksheet.LastColumn;
            if (maxRow <= 0 || maxCol <= 0) //empty sheet
            {
                return;
            }
            // -------------------------------
            // 1. Detect meaningful boundaries
            // -------------------------------
            int endRow = DetectEndRow(worksheet, maxRow, maxCol);
            int endCol = DetectEndColumn(worksheet, maxRow, maxCol);

            // Fallback safety
            if (endRow == 0) endRow = Math.Min(maxRow, 50);
            if (endCol == 0) endCol = Math.Min(maxCol, 10);

            string endCell = ColumnIndexToName(endCol) + endRow;

            // -------------------------------
            // 2. Set Print Area
            // -------------------------------
            worksheet.PageSetup.PrintArea = $"A1:{endCell}";

            // -------------------------------
            // 3. Auto-fit content
            // -------------------------------
            worksheet.Range[$"A1:{endCell}"].AutoFitColumns();
            
            for (int col = 1; col <= endCol; col++)
            {
                double width = worksheet.GetColumnWidth(col);

                if (width > 50)
                {
                    worksheet.SetColumnWidth(col, 50);
                }
            }

            // -------------------------------
            // 4. Decide layout strategy
            // -------------------------------
            //bool isSmallSheet = (endRow <= 60 && endCol <= 12);
            //bool isWideSheet = (endCol > 12 && endCol <= 25);
            //bool isLargeSheet = (endRow > 60 || endCol > 25);

            worksheet.PageSetup.Zoom = 100; // enable FitToPages
            worksheetDimension Dim = getWorksheetDimensions(worksheet, endRow, endCol);
            double marginWidth = (worksheet.PageSetup.LeftMargin + worksheet.PageSetup.RightMargin) * 72;
            const double RowHeaderWidth = 30;
            double pageWidth = Dim.width + marginWidth + RowHeaderWidth;


            if (endCol <= 12)
            {
                worksheet.PageSetup.PaperSize = PaperSizeType.PaperA4;
            }
            else if (endCol <= 20)
            {
                worksheet.PageSetup.PaperSize = PaperSizeType.PaperA3;
            }
            else
            {
                worksheet.PageSetup.PaperSize = PaperSizeType.A2Paper;
            }

            

            worksheet.PageSetup.FitToPagesWide = 1;
            worksheet.PageSetup.FitToPagesTall = 0;

            /*
            if (isSmallSheet)
            {
                // Best case → single page
                worksheet.PageSetup.FitToPagesWide = 1;
                worksheet.PageSetup.FitToPagesTall = 0;  //initially 1
            }
            else if (isWideSheet)
            {
                // Fit width, allow vertical flow
                worksheet.PageSetup.FitToPagesWide = 1;
                worksheet.PageSetup.FitToPagesTall = 0;
            }
            else if (isLargeSheet)
            {
                // Too big → avoid shrinking too much
                worksheet.PageSetup.FitToPagesWide = 1;
                worksheet.PageSetup.FitToPagesTall = 0;
            }
            */
            // -------------------------------
            // 5. Improve readability
            // -------------------------------
            worksheet.PageSetup.Orientation =  endCol <= 8
                ? PageOrientationType.Portrait
                : PageOrientationType.Landscape;
            worksheet.PageSetup.IsPrintGridlines = true;
            worksheet.PageSetup.IsPrintHeadings = true;

            worksheet.PageSetup.LeftMargin = 0.3;
            worksheet.PageSetup.RightMargin = 0.3;
            worksheet.PageSetup.TopMargin = 0.3;
            worksheet.PageSetup.BottomMargin = 0.3;
            worksheet.PageSetup.PrintTitleRows = "";
            worksheet.PageSetup.PrintTitleColumns = "";
        }

        private static worksheetDimension getWorksheetDimensions(Worksheet worksheet, int endRow, int endCol)
        {
            double totalWidth = 0;
            double totalHeight = 0;
            for (int col = 1; col <= endCol; col++)
            {
                totalWidth += worksheet.GetColumnWidth(col);
            }

            for (int row = 1; row <= endRow; row++)
            {
                totalHeight += worksheet.GetRowHeight(row);
            }
            return new worksheetDimension(totalWidth, totalHeight);
        }
        private static int DetectEndRow(Worksheet ws, int maxRow, int maxCol)
        {
            int emptyStreak = 0;
            int lastDataRow = 0;

            for (int r = 1; r <= maxRow; r++)
            {
                bool hasData = false;

                for (int c = 1; c <= maxCol; c++)
                {
                    if (!string.IsNullOrWhiteSpace(ws[r, c].Text))
                    {
                        hasData = true;
                        break;
                    }
                }

                if (hasData)
                {
                    lastDataRow = r;
                    emptyStreak = 0;
                }
                else
                {
                    emptyStreak++;
                }

                // Stop after long empty gap → ignores stray clusters far away
                if (emptyStreak >= 10)
                    break;
            }

            return lastDataRow;
        }
        private static int DetectEndColumn(Worksheet ws, int maxRow, int maxCol)
        {
            int emptyStreak = 0;
            int lastDataCol = 0;

            for (int c = 1; c <= maxCol; c++)
            {
                bool hasData = false;

                for (int r = 1; r <= maxRow; r++)
                {
                    if (!string.IsNullOrWhiteSpace(ws[r, c].Text))
                    {
                        hasData = true;
                        break;
                    }
                }

                if (hasData)
                {
                    lastDataCol = c;
                    emptyStreak = 0;
                }
                else
                {
                    emptyStreak++;
                }

                if (emptyStreak >= 10)
                    break;
            }

            return lastDataCol;
        }
        private static string ColumnIndexToName(int index)
        {
            string name = "";
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                name = (char)(65 + rem) + name;
                index = (index - 1) / 26;
            }
            return name;
        }
        private void ProcessExcel(string filepath, string cacheFolder)    
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_S"));
                //Create an instance of Workbook class
                Workbook workbook = new Workbook();
                //Load an Excel file
                workbook.LoadFromFile(filepath);
                foreach (Worksheet worksheet in workbook.Worksheets)
                {
                    PrepareWorksheetForPdf(worksheet);
                }
                using var pdfStream = new MemoryStream();
                workbook.SaveToStream(pdfStream, Spire.Xls.FileFormat.PDF);
                _logger.LogInfo("Saved portable version in stream");
                // Rewind before reading
                pdfStream.Position = 0;
                using var pdfDocument = new Spire.Pdf.PdfDocument(pdfStream);
                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                workbook.Dispose();
                _logger.LogInfo("Converting portable version in cache...");
                // XOR the PDF stream
                pdfStream.Position = 0;
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw (ex);
            }
        }
        private async Task ProcessPowerPoint(string filepath, string cacheFolder)
        {
            try
            {
                Presentation ppt = new Presentation();
                ppt.LoadFromFile(filepath);
                ToPdfParameterList pdfParams = new ToPdfParameterList();
                pdfParams.PreserveFormFields = true;
                // Convert to PDF and store in cache folder
                string tempPdf = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
                // Save the Presentation file to PDF
                ppt.SaveToFile(tempPdf, Spire.Presentation.FileFormat.PDF);
                var pdfBytes = await File.ReadAllBytesAsync(tempPdf);
                File.Delete(tempPdf);
                using var pdfStream = new MemoryStream(pdfBytes);
                pdfStream.Position = 0;
                _logger.LogInfo("Saved pdf version in stream...");
                using var pdfDocument = new Spire.Pdf.PdfDocument(pdfStream);
                _logger.LogInfo("Creating Pdf Document...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                ppt.Dispose();
                pdfStream.Position = 0;
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
                _logger.LogInfo("Converting portable version in cache...");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw( ex );
            }
        }
        private void ProcessPDF(string filepath, string cacheFolder)
        {
            try
            {
                PdfDocument pdfDocument = new PdfDocument(filepath);
                using var pdfStream = new MemoryStream();
                pdfDocument.SaveToStream(pdfStream, Spire.Pdf.FileFormat.PDF);
                _logger.LogInfo("Saved portable version in stream");
                // Rewind before reading
                pdfStream.Position = 0;
                
                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                pdfDocument.Dispose();
                _logger.LogInfo("Converting portable version in cache...");
                // XOR the PDF stream
                
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw( ex );
            }
        }
        
        private void ProcessText(string filepath, string cacheFolder)
        {
            try
            {
                string text;
                float Margin = 40f;
                using (var reader1 = new StreamReader(filepath, Encoding.UTF8, true))
                {
                    text = reader1.ReadToEnd();
                }
                // Normalize line endings
                text = text.Replace("\r\n", "\n").Replace('\r', '\n');
                PdfDocument document = new PdfDocument();
                PdfPageBase page = document.Pages.Add(PdfPageSize.A4);
                PdfTrueTypeFont font = new PdfTrueTypeFont("Consolas", 11f, PdfFontStyle.Regular, true);
                PdfBrush brush = PdfBrushes.Black;
                float pageWidth = page.Canvas.ClientSize.Width;
                float pageHeight = page.Canvas.ClientSize.Height;
                float usableWidth = pageWidth - (Margin * 2);
                float y = Margin;
                // Fixed line height
                float lineHeight = font.MeasureString("Ag").Height + 2;
                //string[] lines = text.Split('\n');
                using StringReader reader = new StringReader(text);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    //foreach (string line in lines)
                    //{
                    // Preserve blank lines
                    if (line.Length == 0)
                    {
                        if (y + lineHeight > pageHeight - Margin)
                        {
                            page = document.Pages.Add(PdfPageSize.A4);
                            y = Margin;
                            pageWidth = page.Canvas.ClientSize.Width;
                            pageHeight = page.Canvas.ClientSize.Height;
                        }
                        y += lineHeight;
                        continue;
                    }

                    foreach (string wrappedLine in WrapLine(line, font, usableWidth))
                    {
                        if (y + lineHeight > pageHeight - Margin)
                        {
                            page = document.Pages.Add(PdfPageSize.A4);
                            y = Margin;
                            pageWidth = page.Canvas.ClientSize.Width;
                            pageHeight = page.Canvas.ClientSize.Height;
                        }
                        page.Canvas.DrawString(
                            wrappedLine,
                            font,
                            brush,
                            new PointF(Margin, y));
                        y += lineHeight;
                    }
                }
                using var pdfStream = new MemoryStream();
                document.SaveToStream(pdfStream, Spire.Pdf.FileFormat.PDF);
                //document.Close();
                _logger.LogInfo("Saved portable version in stream");
                // Rewind before reading
                pdfStream.Position = 0;
                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(document, cacheFolder, filepath);
                // Rewind again because PdfDocument may have advanced the stream
                pdfStream.Position = 0;
                // XOR the PDF stream
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
                document.Close();
                document.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw (ex);
            }
        }

        private void ProcessHtml(string filepath, string cacheFolder)
        {
            
            try
            {
                HtmlToPdfRequest request = new HtmlToPdfRequest();
                request.EnableJavaScript = true;
                request.HtmlPath = filepath;
                request.Margins = new PdfMargins(0, 2, 0, 2);
                request.PageSize = new SizeF(600, 842);
                request.Timeout = 100000;
                using Stream pdfStream = _htmlToPdfConverter.Convert(request);
                pdfStream.Position = 0;
                using PdfDocument pdfDocument = new PdfDocument(pdfStream);
                pdfStream.Position = 0;

                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                // Rewind again because PdfDocument may have advanced the stream
                pdfStream.Position = 0;
                //var destinationPath1 = System.IO.Path.Combine(cacheFolder, "convert.pdf");
                //using (FileStream file = File.Create(destinationPath1))
                //{
                //    // Rewind before upload
                //    pdfStream.Position = 0;
                //    pdfStream.CopyTo(file);
                //}

                // XOR the PDF stream
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
                pdfDocument.Dispose();
                pdfStream.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
            
        }

        private void ProcessEmail(string filepath, string cacheFolder)
        {
            try
            {
                var filenameonly = Path.GetFileNameWithoutExtension(filepath);
                var extension = Path.GetExtension(filepath).ToLower().Replace(".", "");
                var AttachmentPath = Path.Combine(Path.GetDirectoryName(filepath), $"attach_{filenameonly}_{extension}");
                if (!Directory.Exists(AttachmentPath))
                    Directory.CreateDirectory(AttachmentPath);
                using Stream emailStream = ConvertEmlToHtml(filepath, AttachmentPath);
                //////
                HtmlToPdfRequest request = new HtmlToPdfRequest();
                request.EnableJavaScript = true;
                request.HtmlStream = emailStream;
                request.Margins = new PdfMargins(0, 2, 0, 2);
                request.PageSize = new SizeF(600, 842);
                request.Timeout = 100000;
                using Stream pdfStream = _htmlToPdfConverter.Convert(request);
                pdfStream.Position = 0;
                using PdfDocument pdfDocument = new PdfDocument(pdfStream);
                pdfStream.Position = 0;

                _logger.LogInfo("Processing Pdf for other formats...");
                ProcessPDFForOtherFormats(pdfDocument, cacheFolder, filepath);
                // Rewind again because PdfDocument may have advanced the stream
                pdfStream.Position = 0;
                //var destinationPath1 = System.IO.Path.Combine(cacheFolder, "convert.pdf");
                //using (FileStream file = File.Create(destinationPath1))
                //{
                //    // Rewind before upload
                //    pdfStream.Position = 0;
                //    pdfStream.CopyTo(file);
                //}
                
                // XOR the PDF stream
                using var xorStream = new MemoryStream();
                XorStream(pdfStream, xorStream, 0xAA);
                var destinationPath = System.IO.Path.Combine(cacheFolder, "convert.dat");
                using (FileStream file = File.Create(destinationPath))
                {
                    // Rewind before upload
                    xorStream.Position = 0;
                    xorStream.CopyTo(file);
                }
                pdfDocument.Dispose();
                pdfStream.Dispose();
                //////
                ///
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        public static Stream ConvertEmlToHtml(string sourcePath, string attachmentFolder)
        {
            // Load the email
            Spire.Email.MailMessage message = Spire.Email.MailMessage.Load(sourcePath);

            StringBuilder html = new StringBuilder();

            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html>");
            html.AppendLine("<head>");
            html.AppendLine("<meta charset=\"utf-8\"/>");

            html.AppendLine("<style>");
            html.AppendLine("body{font-family:Arial,Helvetica,sans-serif;font-size:14px;margin:30px;}");
            html.AppendLine("table{width:100%;border-collapse:collapse;}");
            html.AppendLine("td{padding:4px;vertical-align:top;}");
            html.AppendLine("hr{margin-top:15px;margin-bottom:15px;}");
            html.AppendLine(".header{background:#f3f3f3;padding:10px;border:1px solid #ddd;}");
            html.AppendLine(".attachments{margin-top:15px;}");
            html.AppendLine("</style>");

            html.AppendLine("</head>");
            html.AppendLine("<body>");

            html.AppendLine("<div class='header'>");

            html.AppendLine("<table>");

            AddRow(html, "Subject", message.Subject);
            AddRow(html, "From", message.From.ToString());
            AddRow(html, "To", string.Join("; ", message.To));
            AddRow(html, "CC", string.Join("; ", message.Cc));
            AddRow(html, "Date", message.Date.ToString());

            html.AppendLine("</table>");

            // ---------- Attachments moved here ----------
            if (message.Attachments.Count > 0)
            {
                html.AppendLine("<div class='attachments'>");
                html.AppendLine("<b>Attachments:</b>");
                html.AppendLine("<ul>");

                Directory.CreateDirectory(attachmentFolder);
                int counter = 0;
                foreach (var attachment in message.Attachments)
                {
                    string fileName = attachment.FileName;
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = attachment.ContentType?.Name;
                        if (string.IsNullOrWhiteSpace(fileName))
                        {
                            fileName = $"attachment_{counter++}.bin";
                        }
                    }
                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        fileName = fileName.Replace(c, '_');
                    }
                    html.AppendLine(
                        $"<li>{WebUtility.HtmlEncode(fileName)}</li>");

                    string outputPath = Path.Combine(attachmentFolder, fileName);

                    attachment.Data.Position = 0;

                    using FileStream fileStream = File.Create(outputPath);
                    attachment.Data.CopyTo(fileStream);
                }

                html.AppendLine("</ul>");
                html.AppendLine("</div>");
            }

            html.AppendLine("</div>");

            html.AppendLine("<hr/>");

            if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            {
                html.AppendLine(message.BodyHtml);
            }
            else
            {
                html.AppendLine("<pre>");
                html.AppendLine(WebUtility.HtmlEncode(message.BodyText));
                html.AppendLine("</pre>");
            }

            html.AppendLine("</body>");
            html.AppendLine("</html>");

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(html.ToString()));
            stream.Position = 0;

            return stream;
        }
        private static void AddRow(StringBuilder html, string title, string value)
        {
            html.AppendLine("<tr>");
            html.AppendLine($"<td style='width:90px'><b>{title}</b></td>");
            html.AppendLine($"<td>{WebUtility.HtmlEncode(value ?? "")}</td>");
            html.AppendLine("</tr>");
        }
        private static IEnumerable<string> WrapLine(string line, PdfTrueTypeFont font, float maxWidth)
        {
            if (string.IsNullOrEmpty(line))
            {
                yield return "";
                yield break;
            }

            // Replace tabs with spaces (simple implementation)
            line = line.Replace("\t", "    ");

            int start = 0;

            while (start < line.Length)
            {
                int lastWhitespace = -1;
                int end = start;

                while (end < line.Length)
                {
                    if (char.IsWhiteSpace(line[end]))
                        lastWhitespace = end;

                    string candidate = line.Substring(start, end - start + 1);

                    if (font.MeasureString(candidate).Width > maxWidth)
                        break;

                    end++;
                }

                // Entire remaining string fits
                if (end == line.Length)
                {
                    yield return line.Substring(start);
                    yield break;
                }

                // Prefer breaking at whitespace
                if (lastWhitespace >= start)
                {
                    yield return line.Substring(start, lastWhitespace - start);

                    start = lastWhitespace + 1;

                    // Skip consecutive spaces at the beginning of the next line
                    while (start < line.Length && line[start] == ' ')
                        start++;
                }
                else
                {
                    // No whitespace found -> force character split
                    if (end == start)
                        end++;

                    yield return line.Substring(start, end - start);
                    start = end;
                }
            }
        }

        
        public static void XorFileStream(string input, string output, byte key)
        {
            const int bufferSize = 81920; // 80 KB
            byte[] buffer = new byte[bufferSize];

            using (FileStream inStream = new FileStream(input, FileMode.Open, FileAccess.Read))
            using (FileStream outStream = new FileStream(output, FileMode.Create, FileAccess.Write))
            {
                int bytesRead;
                while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < bytesRead; i++)
                    {
                        buffer[i] ^= key;
                    }
                    outStream.Write(buffer, 0, bytesRead);
                }
            }
        }

        public static void XorStream(Stream input, Stream output, byte key)
        {
            const int bufferSize = 81920; // 80 KB
            byte[] buffer = new byte[bufferSize];

            int bytesRead;
            while ((bytesRead = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    buffer[i] ^= key;
                }

                output.Write(buffer, 0, bytesRead);
            }
        }
        private void ProcessPDFForOtherFormats(string filepath, string cacheFolder, string actualFilePath)
        {
            try
            {
                PdfDocument pdfDocument = new PdfDocument(filepath);
                int count = pdfDocument.Pages.Count;
                PdfToSVGConverter(filepath, cacheFolder, pdfDocument);
                PdfToThmConverter(filepath, cacheFolder, pdfDocument, count);
                string indexPath = System.IO.Path.Combine(cacheFolder, "indexpage.json");
                string FName = System.IO.Path.GetFileName(actualFilePath);
                saveIndexFile(indexPath, pdfDocument, FName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(actualFilePath, TPath, count);
                pdfDocument.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private void ProcessPDFForOtherFormats(PdfDocument pdfDocument, string cacheFolder, string actualFilePath)
        {
            try
            {
                //PdfDocument pdfDocument = new PdfDocument(filepath);
                int count = pdfDocument.Pages.Count;
                PdfToSVGConverter(actualFilePath, cacheFolder, pdfDocument);
                PdfToThmConverter(actualFilePath, cacheFolder, pdfDocument, count);
                string indexPath = System.IO.Path.Combine(cacheFolder, "indexpage.json");
                string FName = System.IO.Path.GetFileName(actualFilePath);
                saveIndexFile(indexPath, pdfDocument, FName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(actualFilePath, TPath, count);
                pdfDocument.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private void PdfToSVGConverter(string source, string dest, PdfDocument doc)
        {
            try
            {
                int remainder = 0;
                int EndIndex = 200;
                int startIndex = 0;
                int loppcount = 0;
                int loopindex = 200;
                int totalpage = doc.Pages.Count;
                Directory.CreateDirectory(System.IO.Path.Combine(dest, "_S"));
                if (totalpage >= 200)
                {
                    loppcount = totalpage / 200;
                    remainder = totalpage % 200;
                    for (int l = 0; l <= loppcount; l++)
                    {
                        Stream[] s = doc.SaveToStream(startIndex, (EndIndex - 1), Spire.Pdf.FileFormat.SVG);
                        for (int i = 0; i <= (loopindex - 1); i++)
                        {
                            int j = startIndex + i + 1;
                            File.WriteAllBytes(System.IO.Path.Combine(dest, "_S", string.Format("{0}", j)), StreamToByteArray(s[i]));
                            GC.Collect(GC.MaxGeneration);
                        }
                        int compare = totalpage - EndIndex;
                        if (compare > 0)
                        {
                            if ((compare > 200) || (compare == 200))
                            {
                                startIndex = EndIndex;
                                EndIndex = EndIndex + 200;
                                loopindex = 200;
                            }
                            else
                            {
                                startIndex = EndIndex;
                                EndIndex = EndIndex + compare;
                                loopindex = compare;
                            }
                        }
                        else
                            break;
                    }
                }
                else
                {
                    Stream[] s = doc.SaveToStream(0, (totalpage - 1), Spire.Pdf.FileFormat.SVG);
                    for (int i = 0; i <= (totalpage - 1); i++)
                    {
                        int j = i + 1;
                        File.WriteAllBytes(System.IO.Path.Combine(dest, "_S", string.Format("{0}", j)), StreamToByteArray(s[i]));
                        GC.Collect(GC.MaxGeneration);
                    }
                }
                _logger.LogInfo($"SVG Conversion successful for {source}");
            }
            catch (Exception e) 
            {
                _logger.LogErrorEx(e, $"Error: {e.Message}");
            }


        }

        
        public static byte[] StreamToByteArray(Stream input)
        {
            byte[] buffer = new byte[16 * 1024];
            using (MemoryStream ms = new MemoryStream())
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
        }
        private void PdfToThmConverter(string source, string dest, PdfDocument doc, int count)
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Stream imges = doc.SaveAsImage(i, Spire.Pdf.Graphics.PdfImageType.Bitmap); //   .SaveImageToStreams(i, 1, 0);

                    if (imges != null)
                    {
                        byte[] byteStr = ResizeImage(imges, 150, 150);
                        Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
                        File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
                    }
                }
                _logger.LogInfo($"Thumbnail conversion successful for {source}");
            }
            catch(Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private void saveIndexFile(string indexPath, PdfDocument doc, string FName)
        {
            try
            {
                int totalpage = doc.Pages.Count; 
                Indexdetails[] objIdetailsarr = new Indexdetails[totalpage];
                for (int i = 0; i < totalpage; i++)
                {
                    Indexdetails objindexdetails = new Indexdetails();
                    SizeF pagesize = doc.Pages[i].ActualSize;
                    PdfPageRotateAngle rot = doc.Pages[i].Rotation;
                    string rotStr = rot.ToString();
                    int angle = Convert.ToInt32(rotStr.Replace("RotateAngle", ""));
                    bool interchange = (angle / 90) % 2 == 1 ? true : false;
                    objindexdetails.page = i + 1;
                    objindexdetails.Width = interchange ? pagesize.Height.ToString() : pagesize.Width.ToString(); // decWidth.ToString();
                    objindexdetails.Height = interchange ? pagesize.Width.ToString() : pagesize.Height.ToString(); //decHeight.ToString();
                    objindexdetails.angle = angle;
                    objIdetailsarr[i] = objindexdetails;
                }
                var result = new
                {
                    File = FName,
                    Pages = objIdetailsarr
                };
                string jsonstr = JsonConvert.SerializeObject(result);
                File.WriteAllText(indexPath, jsonstr);
                _logger.LogInfo($"Index file creation successful at {indexPath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private void saveTFile(string FilePath, string TPath, int tot_pages)
        {
            try
            {
                DateTime? dtObj = File.GetLastWriteTimeUtc(FilePath);
                string dtObjStr = string.Empty;
                if (dtObj != null)
                    dtObjStr = makeTimeStampString((DateTime)dtObj);
                string TText = dtObjStr + "|" + tot_pages.ToString() + "|" + "false";
                File.WriteAllText(TPath, TText);
                _logger.LogInfo($"Timestamp file saved successfully at {TPath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        public static string makeTimeStampString(DateTime lastModified) //added on 13-Sep-2018 by Sabyasachi
        {
            return Convert.ToString(lastModified.Day) + "/" + Convert.ToString(lastModified.Month) + "/" + Convert.ToString(lastModified.Year) + " " + Convert.ToString(lastModified.Hour) + ":" + Convert.ToString(lastModified.Minute) + ":" + Convert.ToString(lastModified.Second);
        }
        public async Task<string> OCConvertAndReturnId(string filepath, string cacheFolder)
        {
            try
            {
                _logger.LogInfo($"Into StartOC Function");
                string extension = System.IO.Path.GetExtension(filepath).Replace(".", "").ToLower();
                string OCID = string.Empty;
                var urlf = _paths.OCUrl + "files";
                var urlj = _paths.OCUrl + "jobs";
                var jsonresp = string.Empty;
                using (var cancellationTokenSource = new CancellationTokenSource())
                {
                    cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(300));
                    jsonresp = await PostURI(urlf, filepath, cancellationTokenSource.Token);
                }
                if (string.IsNullOrEmpty(jsonresp))
                {
                    throw new Exception("OpenCloud could not upload the given file");
                }
                var JsonObj = JsonConvert.DeserializeObject<dynamic>(jsonresp);
                if (JsonObj == null)
                {
                    throw new Exception("OpenCloud internal server error.");
                }
                OCID = JsonObj.id.Value;
                string name = JsonObj.name.Value;
                await PostJob(OCID, urlj, 1);
                await PostJob(OCID, urlj, 2);
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                    await PostJob(OCID, urlj, 3);
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf") || extension.ToLower().Equals("dgn"))
                    await PostJob(OCID, urlj, 4);
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf")) // || extension.ToLower().Equals(".dxf") || extension.ToLower().Equals(".dgn"))
                    await PostJob(OCID, urlj, 5);
                
                string OCStr = OCID + "|" + name;
                string OCPath = System.IO.Path.Combine(cacheFolder, "_OC");
                File.WriteAllText(OCPath, OCStr);
                return OCID;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private async Task<bool> FetchOCFiles(ProcessingJob job)
        {
            var ext = System.IO.Path.GetExtension(job.FilePath).Replace(".", "").ToLower();

            var tasks = new List<Task<bool>>();

            if (ext == "dwg" || ext == "dxf")
            {
                tasks.Add(fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "textdata"));
                tasks.Add(fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "xdata"));
                tasks.Add(fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "thumbnail.bmp"));
            }

            var results = await Task.WhenAll(tasks);

            bool success = results.All(r => r);

            if (!success)
            {
                _logger.LogWarning($"Some OC files failed to fetch for {job.FilePath}");
            }

            return success; // ✅ key fix
        }
        public async Task<string> OCConvertAndReturnId(string filepath, string cacheFolder, string[] XRefPaths)
        {
            try
            {
                var extension = System.IO.Path.GetExtension(filepath).Replace(".", "").ToLower();
                string OCPath = System.IO.Path.Combine(cacheFolder, "_OC");
                string OCID = string.Empty;
                string OCID1 = string.Empty;
                var urlf = _paths.OCUrl + "files";
                var urlj = _paths.OCUrl + "jobs";
                var jsonresp = string.Empty;
                using (var cancellationTokenSource = new CancellationTokenSource())
                {
                    cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));
                    jsonresp = await PostURI(urlf, filepath, cancellationTokenSource.Token);
                }
                if (jsonresp == string.Empty)
                {
                    throw new Exception("OpenCloud could not upload the given file");
                }
                var result = jsonresp;
                var JsonObj = JsonConvert.DeserializeObject<dynamic>(result);
                if (JsonObj == null)
                {
                    throw new Exception("OpenCloud internal server error.");
                }
                OCID = JsonObj.id.Value;
                string name = JsonObj.name.Value;
                int refCount = XRefPaths.Length;
                refObj[] OCIDRefs = new refObj[refCount];
                for (int i = 0; i < refCount; i++)
                {
                    var jsonresp1 = string.Empty;
                    using (var cancellationTokenSource = new CancellationTokenSource())
                    {
                        cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(300));
                        jsonresp1 = await PostURI(urlf, XRefPaths[i], cancellationTokenSource.Token); //
                    }
                    var JsonObj1 = JsonConvert.DeserializeObject<dynamic>(jsonresp1);
                    refObj temp = new refObj();
                    temp.fname = JsonObj1.name.Value;
                    temp.id = JsonObj1.id.Value;
                    OCIDRefs[i] = temp;
                }
                await PutJob(OCID, OCIDRefs, filepath);
                await PostJob(OCID, urlj, 1);
                await PostJob(OCID, urlj, 2);
                if (extension.Equals("dwg") || extension.Equals("dxf"))
                    await PostJob(OCID, urlj, 3);
                if (extension.Equals("dwg") || extension.Equals("dxf") || extension.Equals("dgn"))
                    await PostJob(OCID, urlj, 4);
                if (extension.Equals("dwg") || extension.Equals("dxf")) // || extension.ToLower().Equals(".dxf") || extension.ToLower().Equals(".dgn"))
                    await PostJob(OCID, urlj, 5);
                
                string OCStr = OCID + "|" + name;
                File.WriteAllText(OCPath, OCStr, Encoding.UTF8);
                _logger.LogInfo($"OC Conversion successful for {filepath}");
                return OCID;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private async Task<string> StartOCWithXRefs(ProcessingJob job)
        {
            try
            {
                string cacheFolder = job.CacheFolder;
                string filepath = job.FilePath;

                string xPath = System.IO.Path.Combine(cacheFolder, "_x.txt");

                if (File.Exists(xPath))
                {
                    _logger.LogInformation($"Key {xPath} Exists.");

                    string xpathStr = await File.ReadAllTextAsync(xPath);

                    if (string.IsNullOrWhiteSpace(xpathStr))
                    {
                        _logger.LogInformation("XRef file empty. Proceeding normally.");
                        string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                        saveTFile(filepath, TPath, 1);
                        return await OCConvertAndReturnId(filepath, cacheFolder);
                    }
                    else
                    {
                        string[] xRefs = xpathStr.Split('|', StringSplitOptions.RemoveEmptyEntries);
                        string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                        saveTFile(filepath, TPath, 1);
                        return await OCConvertAndReturnId(filepath, cacheFolder, xRefs);
                    }
                }
                else
                {
                    _logger.LogInformation($"Key {xPath} does not exist. Processing normally.");
                    string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                    saveTFile(filepath, TPath, 1);
                    return await OCConvertAndReturnId(filepath, cacheFolder);
                }
            }
            catch (Exception ex) 
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw (ex);
            }
        }
        private async Task PutJob(String Id, refObj[] reference, string filepath)
        {
            try
            {
                string job = "{references:[";
                string ref_url = _paths.OCUrl + "files/" + Id + "/references";
                for (int i = 0; i < reference.Length; i++)
                {
                    string ref_filename = reference[i].fname;
                    string ref_Id = reference[i].id;
                    if (i < reference.Length - 1)
                        job = job + "{\"name\":\"" + ref_filename + "\",\"id\":\"" + ref_Id + "\"},";
                    else
                        job = job + "{\"name\":\"" + ref_filename + "\",\"id\":\"" + ref_Id + "\"}";
                }
                job = job + "]}";
                var content = new StringContent(job, Encoding.UTF8, "application/json");
                using (var openClClient = new HttpClient())
                {
                    openClClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                    openClClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
                    openClClient.DefaultRequestHeaders.Add("Authorization", _paths.TokenNumber);
                    HttpResponseMessage response = await openClClient.PutAsync(ref_url, content);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInfo($"Linked Successfully for {filepath}");
                    }
                    else
                    {
                        throw new Exception($"Error: {response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private async Task<string> PostURI(string url, string filePath, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInfo($"Into PostURI for url: {url} and Token number: {_paths.TokenNumber}");
                // Prepare the multipart form data content
                using (var content = new MultipartFormDataContent())
                using (var httpClient = new HttpClient())
                {
                    // Set request headers
                    httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                    httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
                    httpClient.DefaultRequestHeaders.Add("Authorization", _paths.TokenNumber);

                    // Add file content to the request
                    var fileName = System.IO.Path.GetFileName(filePath);
                    using (var fileStream = File.OpenRead(filePath))
                    {
                        var fileContent = new StreamContent(fileStream);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                        content.Add(fileContent, "File", fileName);

                        // Send the POST request
                        using (HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken))
                        {
                            _logger.LogInfo(response.StatusCode.ToString());
                            response.EnsureSuccessStatusCode(); // Throws if the status code is not successful

                            // Read and return the response content
                            string responseString = await response.Content.ReadAsStringAsync();
                            return responseString;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private async Task<string> PostJob(string Id, string url, int type)
        {
            try
            {
                // Determine job content based on the type
                string job = GetJobJson(Id, type);

                if (string.IsNullOrEmpty(job))
                {
                    throw new ArgumentException("Invalid job type provided.");
                }

                var content = new StringContent(job, Encoding.UTF8, "application/json");

                // Use HttpClient as a reusable instance
                using (var openClClient = new HttpClient())
                {
                    openClClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
                    openClClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
                    openClClient.DefaultRequestHeaders.Add("Authorization", _paths.TokenNumber); // Example token

                    using (HttpResponseMessage response = await openClClient.PostAsync(url, content))
                    {
                        // Ensure successful status code (throws exception if not successful)
                        response.EnsureSuccessStatusCode();

                        // Read and return the response content
                        string responseString = await response.Content.ReadAsStringAsync();
                        return responseString;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private string GetJobJson(string Id, int type)
        {
            try
            {
                // A dictionary could also be used here to map type to templates if necessary
                switch (type)
                {
                    case 1:
                        return $"{{\"fileId\": \"{Id}\",\"outputFormat\":\"geometry\",  \"parameters\": {{}}}}";
                    case 2:
                        return $"{{\"fileId\": \"{Id}\",\"outputFormat\":\"properties\",  \"parameters\": {{}}}}";
                    case 3:
                        return $"{{\"fileId\": \"{Id}\",\"outputFormat\":\"command\",  \"template\": \"{_paths.RasterJob}\"}}";
                    case 4:
                        return $"{{\"fileId\": \"{Id}\",\"outputFormat\":\"command\",  \"template\": \"{_paths.TextJob}\"}}";
                    case 5:
                        return $"{{\"fileId\": \"{Id}\",\"outputFormat\":\"command\",  \"template\": \"{_paths.XDataJob}\"}}";
                    default:
                        return null;
                }
            }
            catch(Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: ex.Message");
                throw;
            }
        }
        private async Task<bool> fetchOCFiles(string filepath, string cacheFolder, string OCID, string token, string fnam)
        {
            try
            {
                string destinationPath = "";

                if (fnam == "textdata")
                {
                    Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Txt"));
                    destinationPath = System.IO.Path.Combine(cacheFolder, "_Txt", "1");
                }
                else if (fnam == "xdata")
                {
                    destinationPath = System.IO.Path.Combine(cacheFolder, "xdata");
                }
                else if (fnam == "thumbnail.bmp")
                {
                    Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Thm"));
                    destinationPath = System.IO.Path.Combine(cacheFolder, "_Thm", "1.png");
                }

                if (fnam != "thumbnail.bmp")
                {
                    string jsontext = await getOCFiles(OCID, token, fnam);

                    if (!string.IsNullOrEmpty(jsontext) && !jsontext.StartsWith("S"))
                    {
                        File.WriteAllText(destinationPath, jsontext);
                        _logger.LogInformation($"Fetched {fnam}");
                        return true;
                    }

                    return false;
                }
                else
                {
                    var obj = await getOCFilesBinary(OCID, token, fnam);

                    if (obj?.content != null)
                    {
                        File.WriteAllBytes(destinationPath, obj.content);
                        _logger.LogInformation("Thumbnail fetched");
                        return true;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error fetching {fnam}: {ex.Message}");
                return false;
            }
        }
        async private Task<String> getOCFiles(string id, string token, string fname)
        {
            try
            {
                String retStr;
                using (var httpClient = new HttpClient())
                {
                    using (var request = new HttpRequestMessage(new HttpMethod("GET"), _paths.OCUrl + "files/" + id + "/downloads/" + fname))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", token);
                        var response = await httpClient.SendAsync(request);
                        Stream receiveStream = await response.Content.ReadAsStreamAsync();
                        using (StreamReader reader = new StreamReader(receiveStream))
                            retStr = reader.ReadToEnd();
                    }
                };
                return retStr;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        async private Task<responseObj> getOCFilesBinary(string id, string token, string fname)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    using (var request = new HttpRequestMessage(new HttpMethod("GET"), _paths.OCUrl + "files/" + id + "/downloads/" + fname))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", token);
                        var response = await httpClient.SendAsync(request);

                        if (response.IsSuccessStatusCode)
                        {
                            byte[] data = await response.Content.ReadAsByteArrayAsync();
                            if (data != null && data.Length > 0)
                            {
                                responseObj obj = new responseObj();
                                obj.content = data;
                                obj.responseStatus = response.StatusCode.ToString();
                                return obj;
                            }
                            else
                            {
                                responseObj obj = new responseObj();
                                obj.content = null;
                                obj.responseStatus = response.StatusCode.ToString();
                                return obj;
                            }
                        }
                        else
                        {
                            responseObj obj = new responseObj();
                            obj.content = null;
                            obj.responseStatus = response.StatusCode.ToString();
                            return obj;
                        }
                    }
                };

            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private void ProcessRaster(string filepath, string cacheFolder)
        {
            try
            {
                DimDetails[] DimArr = null;
                var fName = System.IO.Path.GetFileName(filepath);
                byte[] fileBytes = File.ReadAllBytes(filepath);
                var inputStream  = new MemoryStream(fileBytes);
                inputStream.Position = 0;
                var extension = System.IO.Path.GetExtension(filepath).Replace(".", "").ToLower();
                if (IsMultiFrameFormat(extension))
                {
                    DimArr = ProcessMultiFrameImage(inputStream, filepath, cacheFolder);
                }
                else
                {
                    DimArr = ProcessSingleFrameImage(inputStream, filepath, cacheFolder);
                }
                inputStream.Position = 0;
                var indexPath = System.IO.Path.Combine(cacheFolder, "indexpage.json");
                var count = saveIndexFile(indexPath, inputStream, fName, DimArr);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw (ex);
            }
        }
        private bool IsMultiFrameFormat(string extension)
        {
            return extension.ToLower() == "gif" || extension.ToLower() == "tiff" || extension.ToLower() == "tif";
        }
        private int saveIndexFile(string indexPath, MemoryStream inputstream, string FName, DimDetails[] DimArr)
        {
            try
            {
                Indexdetails[] objIdetailsarr = new Indexdetails[DimArr.Length];
                for (int i = 0; i < DimArr.Length; i++)
                {
                    Indexdetails objindexdetails = new Indexdetails();
                    objindexdetails.page = i + 1;
                    objindexdetails.Width = DimArr[i].width.ToString(); 
                    objindexdetails.Height = DimArr[i].height.ToString(); 
                    objindexdetails.angle = 0;
                    objIdetailsarr[i] = objindexdetails;
                }
                string json = JsonConvert.SerializeObject(objIdetailsarr);
                string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
                File.WriteAllText(indexPath, jsonstr);
                return DimArr.Length;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private DimDetails[] ProcessMultiFrameImage(MemoryStream inputStream, string filepath, string cacheFolder)
        {
            try
            {
                
                using var images = new MagickImageCollection(inputStream);
                DimDetails[] DimArr = new DimDetails[images.Count];
                _logger.LogInfo($"Processing multi-frame image with {images.Count} frames");

                for (int i = 0; i < images.Count; i++)
                {
                    using var frame = images[i];
                    DimDetails temp = ProcessAndUploadFrame((MagickImage)frame, cacheFolder, i);
                    DimArr[i] = temp;
                }
                _logger.LogInfo($"Successfully processed multi-frame image and thumbnail with {images.Count} frames for {filepath}");
                return DimArr;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Exception encountered: {ex.Message}");
                throw;
            }
        }
        private DimDetails[] ProcessSingleFrameImage(MemoryStream inputStream, string filepath, string cacheFolder)
        {
            DimDetails[] DimArr = new DimDetails[1];
            try
            {
                using var image = new MagickImage(inputStream);
                DimDetails temp = ProcessAndUploadFrame(image, cacheFolder, null);
                DimArr[0] = temp;
                _logger.LogInfo($"Successfully processed single-frame image and thumbnail with 1 frame for {filepath}");
                return DimArr;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Exception encountered: {ex.Message}");
                throw;
            }
        }
        private DimDetails ProcessAndUploadFrame(MagickImage image, string cacheFolderPath, int? frameIndex)
        {
            try
            {
                // Create a clone for thumbnail to avoid modifying original image
                //using var thumbnailImage = new MagickImage(image.ToByteArray());
                using var thumbnailImage = image.Clone();
                DimDetails res = GenerateFrameBinaryPage(image, cacheFolderPath, frameIndex);
                GeneratePNGThumbnail((MagickImage)thumbnailImage, cacheFolderPath, frameIndex);
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error processing frame {frameIndex}: {ex.Message}");
                throw;
            }
        }
        private DimDetails GenerateFrameBinaryPage(MagickImage image, string cacheFolder, int? frameIndex)
        {
            try
            {
                // Configure image settings
                DimDetails temp =  ConfigureImageSettings(image);
                
                // Convert to WebP
                image.Format = MagickFormat.WebP;

                // Generate output path for base64
                string outputKey = GenerateRDFrameKeyy(cacheFolder, frameIndex);

                // Convert to base64
                //using var outputStream = new MemoryStream();
                //image.Write(outputStream, MagickFormat.WebP);
                //outputStream.Position = 0;
                //byte[] imageBytes = outputStream.ToArray();
                //string base64String = Convert.ToBase64String(imageBytes);
                byte[] imageBytes = image.ToByteArray(MagickFormat.WebP);
                File.WriteAllBytes(outputKey, imageBytes);
                //string base64String = Convert.ToBase64String(imageBytes);
                //File.WriteAllText(outputKey, base64String);

                _logger.LogInfo($"Successfully uploaded base64 string: {outputKey}");
                return temp;
            }
            catch(Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        private void GeneratePNGThumbnail(MagickImage image, string cacheFolderPath, int? frameIndex)
        {
            try
            {
                // ✅ STEP 1: FAST resize (biggest win)
                if (image.Width > 512 || image.Height > 512)
                {
                    image.Thumbnail(new MagickGeometry(512, 512));
                }

                // ✅ STEP 2: Remove metadata (huge for PNG)
                image.Strip();

                // ✅ STEP 3: Reduce color complexity (massive speed + size gain)
                image.Quality = 75;
                image.Depth = 8; // reduce from 16-bit if present

                // Optional but powerful for thumbnails:
                image.ColorType = ImageMagick.ColorType.Palette; // converts to indexed PNG

                // ✅ STEP 4: Set PNG format
                image.Format = MagickFormat.Png;

                // ✅ STEP 5: Fast PNG write settings
                var defines = new PngWriteDefines
                {
                    CompressionLevel = 5 // 0–9 (tradeoff)
                };
                if (image is MagickImage magickImage)
                {
                    image.Settings.SetDefines(defines);
                    // ✅ Set filter via raw define (compatible way)
                    image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "0");
                    image.Settings.SetDefine(MagickFormat.Png, "compression-level", "5");
                    image.Settings.SetDefine(MagickFormat.Png, "compression-strategy", "1"); // faster
                }
                // ✅ STEP 6: Save
                string thumbnailKey = GenerateThumbnailOutputKey(cacheFolderPath, frameIndex);
                image.Write(thumbnailKey);

                _logger.LogInfo($"Successfully uploaded thumbnail: {thumbnailKey}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private string GenerateThumbnailOutputKey(string cacheFolderPath, int? frameIndex)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(cacheFolderPath, "_Thm")); 
            if (frameIndex.HasValue)
            {
                return System.IO.Path.Combine(cacheFolderPath, "_Thm", string.Format("{0}.png", frameIndex + 1)); 
            }
            return System.IO.Path.Combine(cacheFolderPath, "_Thm", string.Format("{0}.png", 1));
        }
        private DimDetails ConfigureImageSettings(MagickImage image)
        {
            // Resize first
            if (image.Width > 4096 || image.Height > 4096)
            {
                //image.Strip();
                //image.FilterType = FilterType.Triangle; // or Box for even faster
                //image.Resize(new MagickGeometry(4096, 4096));

                //image.Resize(new MagickGeometry(4096, 4096));
                image.Thumbnail(new MagickGeometry(4096, 4096));
            }

            // Only apply if needed
            if (image.ColorSpace != ColorSpace.RGB)
                image.ColorSpace = ColorSpace.RGB;

            if (!image.HasAlpha)
                image.Alpha(AlphaOption.Set);

            var webpSettings = new WebPWriteDefines
            {
                Method = 4,
                Lossless = false,
                AutoFilter = true,
                AlphaQuality = 85
            };

            image.Settings.SetDefines(webpSettings);

            image.Quality = 85;
            return new DimDetails((int)image.Width, (int)image.Height);
        }
        private string GenerateRDFrameKeyy(string cacheFolder, int? frameIndex)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_RD"));
            if (frameIndex.HasValue)
            {
                return System.IO.Path.Combine(cacheFolder, "_RD", string.Format("{0}",frameIndex + 1 ));
            }
            return System.IO.Path.Combine(cacheFolder, "_RD", string.Format("{0}", 1));
        }
    }

    public class responseObj
    {
        public byte[] content { get; set; }
        public string responseStatus { get; set; }
    }

    public class refObj
    {
        public string id { get; set; }
        public string fname { get; set; }

        public refObj()
        {
            this.id = string.Empty;
            this.fname = string.Empty;
        }
        public refObj(string id, string fname)
        {
            this.id = id;
            this.fname = fname;
        }

    }
}

/* -----------------------DEPRECATED CODES---------------------------
  
//private int WordToSVGConverter(string source, string dest, Spire.Doc.Document doc)
        //{
        //    try
        //    {
        //        Queue<byte[]> svgBytes = doc.SaveToSVG();
        //        int len = svgBytes.Count;
        //        string completePath = System.IO.Path.Combine(dest, "_PGC");
        //        File.WriteAllText(completePath, len.ToString());
        //        Directory.CreateDirectory(System.IO.Path.Combine(dest, "_S"));
        //        for (int i = 0; i < len; i++)
        //        {
        //            FileStream fs = new FileStream(System.IO.Path.Combine(dest, "_S", string.Format("{0}", i + 1)), FileMode.Create);
        //            byte[] bytes = svgBytes.Dequeue();
        //            fs.Write(bytes, 0, bytes.Length);

        //        }
        //        _logger.LogInfo($"SVG conversion successful for {source}");
        //        return len;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //        throw;
        //    }

        //}

        //private void WordToThmConverter(string source, string dest, Spire.Doc.Document doc, int count)
        //{
        //    try
        //    {
        //        for (int i = 0; i < count; i++)
        //        {
        //            Stream[] imges = doc.SaveImageToStreams(i, 1, 0);

        //            if (imges[0] != null)
        //            {
        //                byte[] byteStr = ResizeImage(imges[0], 150, 150);
        //                Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
        //                File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
        //            }
        //        }
        //        _logger.LogInfo($"Thumbnail conversion successful for {source}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }

        //}

//private void saveIndexFile(string indexPath, Spire.Doc.Document doc, string FName, DimDetails[] DimArr)
        //{
        //    try
        //    {
        //        int totalpage = doc.PageCount; // svgBytes.Count;
        //        Indexdetails[] objIdetailsarr = new Indexdetails[totalpage];
        //        for (int i = 0; i < totalpage; i++)
        //        {

        //            Indexdetails objindexdetails = new Indexdetails();
        //            objindexdetails.page = i + 1;
        //            objindexdetails.Width = doc.Sections[0].PageSetup.PageSize.Width.ToString();
        //            objindexdetails.Height = doc.Sections[0].PageSetup.PageSize.Height.ToString();//interchange ? pagesize.Width.ToString() : pagesize.Height.ToString(); //decHeight.ToString();
        //            objIdetailsarr[i] = objindexdetails;
        //        }
        //        string json = JsonConvert.SerializeObject(objIdetailsarr);
        //        string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
        //        File.WriteAllText(indexPath, jsonstr); //  await awsLib.writeAllTextToS3Async(C, BName, indexPath, jsonstr);
        //        _logger.LogInfo($"Index file creation successful at {indexPath}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }
        //}

//private void ExcelToSVGConverter(string filepath, string cacheFolder, Workbook workbook)
        //{
        //    try
        //    {
        //        Indexdetails[] objIdetailsarr = new Indexdetails[workbook.Worksheets.Count];
        //        for (int i = 0; i < workbook.Worksheets.Count; i++)
        //        {

        //            Worksheet sheet = workbook.Worksheets[i];
        //            using (var ms = new MemoryStream())
        //            {
        //                sheet.ToSVGStream(ms, 0, 0, 0, 0);
        //                ms.Position = 0;
        //                // Parse SVG size here
        //                var svgDoc = XDocument.Load(ms);
        //                var svg = svgDoc.Root;
        //                Indexdetails objindexdetails = new Indexdetails();
        //                objindexdetails.page = i + 1;
        //                var viewBox = svg.Attribute("viewBox")?.Value;
        //                if (!string.IsNullOrEmpty(viewBox))
        //                {
        //                    var vb = viewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        //                    objindexdetails.Width = double.Parse(vb[2], CultureInfo.InvariantCulture).ToString();
        //                    objindexdetails.Height = double.Parse(vb[3], CultureInfo.InvariantCulture).ToString();
        //                }
        //                else
        //                {
        //                    objindexdetails.Width = ParseSvgLength(svg.Attribute("width")?.Value).ToString();
        //                    objindexdetails.Height = ParseSvgLength(svg.Attribute("height")?.Value).ToString();
        //                }

        //                objIdetailsarr[i] = objindexdetails;
        //                // OPTIONAL: now write to file if needed
        //                File.WriteAllBytes(
        //                    System.IO.Path.Combine(cacheFolder, "_S", $"{i + 1}"),
        //                    ms.ToArray());
        //            }
        //        }
        //        string json = JsonConvert.SerializeObject(objIdetailsarr);
        //        string jsonstr = "{\"File\":\"" + "SampleExcle" + "\"" + "," + "\"Pages\":" + json + "}";
        //        File.WriteAllText(System.IO.Path.Combine(cacheFolder, "indexpage.json"), jsonstr);
        //        _logger.LogInfo($"SVG and Index file creation successful for {filepath}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }
        //}
        //static int ParseSvgLength(string value)
        //{
        //    if (string.IsNullOrWhiteSpace(value))
        //        return 0;

        //    value = value.Trim().ToLowerInvariant();

        //    if (value.EndsWith("px"))
        //        value = value[..^2];

        //    return int.Parse(value, CultureInfo.InvariantCulture);
        //}

        //private void ExcelToThmConverter(string source, string dest, Workbook doc)
        //{
        //    try
        //    {
        //        int count = doc.Worksheets.Count;
        //        for (int i = 0; i < count; i++)
        //        {

        //            Worksheet sheet = doc.Worksheets[i];
        //            // Render worksheet to Image
        //            Stream stream = sheet.ToImage(0, 0, 0, 0);

        //            if (stream != null)
        //            {
        //                byte[] byteStr = ResizeImage(stream, 150, 150);
        //                Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
        //                File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
        //            }
        //        }
        //        _logger.LogInfo($"Thumbnail conversion successful for {source}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }

        //}
//private int PptToSVGConverter(string source, string dest, Presentation ppt)
        //{
        //    try
        //    {
        //        Directory.CreateDirectory(System.IO.Path.Combine(dest, "_S"));
        //        Queue<byte[]> svgBytes = ppt.SaveToSVG();
        //        int len = svgBytes.Count;
        //        string CompletePath = System.IO.Path.Combine(dest, "_PGC");
        //        File.WriteAllText(CompletePath, len.ToString());
        //        for (int i = 0; i < len; i++)
        //        {
        //            FileStream fs = new FileStream(System.IO.Path.Combine(dest, "_S", string.Format("{0}", i + 1)), FileMode.Create);
        //            byte[] bytes = svgBytes.Dequeue();
        //            fs.Write(bytes, 0, bytes.Length);
        //        }
        //        return len;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //        throw;
        //    }
        //}
        //public static void saveIndexFile(string indexPath, Presentation doc, string FName)
        //{
        //    try
        //    {
        //        int totalpage = doc.Slides.Count;
        //        string width = ((int)Math.Round(doc.SlideSize.Size.Width, MidpointRounding.AwayFromZero)).ToString();
        //        string height = ((int)Math.Round(doc.SlideSize.Size.Height, MidpointRounding.AwayFromZero)).ToString();
        //        Indexdetails[] objIdetailsarr = new Indexdetails[totalpage];
        //        for (int i = 0; i < totalpage; i++)
        //        {

        //            Indexdetails objindexdetails = new Indexdetails();
        //            objindexdetails.page = i + 1;
        //            objindexdetails.Width = width;
        //            objindexdetails.Height = height;//interchange ? pagesize.Width.ToString() : pagesize.Height.ToString(); //decHeight.ToString();
        //            objIdetailsarr[i] = objindexdetails;
        //        }
        //        string json = JsonConvert.SerializeObject(objIdetailsarr);
        //        string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
        //        File.WriteAllText(indexPath, jsonstr); //  await awsLib.writeAllTextToS3Async(C, BName, indexPath, jsonstr);

        //    }
        //    catch (Exception ex)
        //    {

        //    }
        //}

        //public static void PptToThmConverter(string source, string dest, Presentation doc)
        //{
            
        //    int count = doc.Slides.Count;
        //    for (int i = 0; i < count; i++)
        //    {
        //        ISlide slide = doc.Slides[i];
        //        // Render slide to Image
        //        Stream image = slide.SaveAsImage();
                
        //        if (image != null)
        //        {
        //            byte[] byteStr = ResizeImage(image, 150, 150);
        //            Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
        //            File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
        //        }
        //    }

        //}

//private async void ProcessCAD(string filepath, string cacheFolder)
        //{
        //    try
        //    {
        //        string xPath = System.IO.Path.Combine(cacheFolder, "_x.txt");
        //        bool is_xFile = File.Exists(xPath); // await awsLib.DoesS3KeyExistAsync(S3Client, sourceBucket, sourcePath.xPath);
        //        if (is_xFile)
        //        {
        //            _logger.LogInfo($"Key {xPath} Exists.");
        //            string xpathStr = File.ReadAllText(xPath);
        //            if (string.IsNullOrEmpty(xpathStr))
        //            {
        //                //treat as normal CAD file.
        //                _logger.LogInfo($"Key {xPath} exists with no content. Proceeding normally ");
        //                await OCConvert(filepath, cacheFolder);
        //                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
        //                saveTFile(filepath, TPath, 1);
        //            }
        //            else
        //            {
        //                string[] XRefPaths = xpathStr.Split('|');
        //                await OCConvert(filepath, cacheFolder, XRefPaths);
        //                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
        //                saveTFile(filepath, TPath, 1);

        //            }
        //        }
        //        else
        //        {
        //            //treat as normal CAD file.
        //            _logger.LogInfo($"Key {xPath} does not exist. Processing normally ");
        //            await OCConvert(filepath, cacheFolder);
        //            string TPath = System.IO.Path.Combine(cacheFolder, "_T");
        //            saveTFile(filepath, TPath, 1);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error in ConvertCADAsync: {ex.StackTrace}");
        //        throw;
        //    }
        //}

        //public async Task OCConvert(string filepath, string cacheFolder)
        //{
        //    try
        //    {
        //        _logger.LogInfo($"Into OCConver Function");
        //        string extension = System.IO.Path.GetExtension(filepath).Replace(".", "").ToLower();
        //        string OCID = string.Empty;
        //        var urlf = _paths.OCUrl + "files";
        //        var urlj = _paths.OCUrl + "jobs";
        //        var jsonresp = string.Empty;
        //        using (var cancellationTokenSource = new CancellationTokenSource())
        //        {
        //            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(300));
        //            jsonresp = await PostURI(urlf, filepath, cancellationTokenSource.Token);
        //        }
        //        if (string.IsNullOrEmpty(jsonresp))
        //        {
        //            throw new Exception("OpenCloud could not upload the given file");
        //        }
        //        var JsonObj = JsonConvert.DeserializeObject<dynamic>(jsonresp);
        //        if (JsonObj == null)
        //        {
        //            throw new Exception("OpenCloud internal server error.");
        //        }
        //        OCID = JsonObj.id.Value;
        //        string name = JsonObj.name.Value;
        //        await PostJob(OCID, urlj, 1);
        //        await PostJob(OCID, urlj, 2);
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //            await PostJob(OCID, urlj, 3);
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf") || extension.ToLower().Equals("dgn"))
        //            await PostJob(OCID, urlj, 4);
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf")) // || extension.ToLower().Equals(".dxf") || extension.ToLower().Equals(".dgn"))
        //            await PostJob(OCID, urlj, 5);
        //        ///////////////////////////////////////////////////////
        //        //fetch textdata, xdata & thumbnail into cache from OC server...not mandatory////
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf") || extension.ToLower().Equals("dgn"))
        //        {
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "textdata");
        //        }

        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //        {
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "xdata");
        //        }

        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //        {
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "thumbnail.bmp");
        //        }
                
                
        //        string OCStr = OCID + "|" + name;
        //        string OCPath = System.IO.Path.Combine(cacheFolder,"_OC");
        //        File.WriteAllText(OCPath, OCStr); 
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //        throw;
        //    }
        //}

        //public async Task OCConvert(string filepath, string cacheFolder, string[] XRefPaths)
        //{
        //    try
        //    {
        //        var extension = System.IO.Path.GetExtension(filepath).Replace(".","").ToLower();
        //        string OCPath = System.IO.Path.Combine(cacheFolder, "_OC"); 
        //        string OCID = string.Empty;
        //        string OCID1 = string.Empty;
        //        var urlf = _paths.OCUrl + "files";
        //        var urlj = _paths.OCUrl + "jobs";
        //        var jsonresp = string.Empty;
        //        using (var cancellationTokenSource = new CancellationTokenSource())
        //        {
        //            cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));
        //            jsonresp = await PostURI(urlf, filepath, cancellationTokenSource.Token); 
        //        }
        //        if (jsonresp == string.Empty)
        //        {
        //            throw new Exception("OpenCloud could not upload the given file");
        //        }
        //        var result = jsonresp; 
        //        var JsonObj = JsonConvert.DeserializeObject<dynamic>(result);
        //        if (JsonObj == null)
        //        {
        //            throw new Exception("OpenCloud internal server error.");
        //        }
        //        OCID = JsonObj.id.Value;
        //        string name = JsonObj.name.Value;
        //        int refCount = XRefPaths.Length;
        //        refObj[] OCIDRefs = new refObj[refCount];
        //        for (int i = 0; i < refCount; i++)
        //        {
        //            var jsonresp1 = string.Empty;
        //            using (var cancellationTokenSource = new CancellationTokenSource())
        //            {
        //                cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(300));
        //                jsonresp1 = await PostURI(urlf, XRefPaths[i], cancellationTokenSource.Token); //
        //            }
        //            var JsonObj1 = JsonConvert.DeserializeObject<dynamic>(jsonresp1);
        //            refObj temp = new refObj();
        //            temp.fname = JsonObj1.name.Value;
        //            temp.id = JsonObj1.id.Value;
        //            OCIDRefs[i] = temp;
        //        }
        //        await PutJob(OCID, OCIDRefs, filepath);
        //        await PostJob(OCID, urlj, 1);
        //        await PostJob(OCID, urlj, 2);
        //        if (extension.Equals("dwg") || extension.Equals("dxf"))
        //            await PostJob(OCID, urlj, 3);
        //        if (extension.Equals("dwg") || extension.Equals("dxf") || extension.Equals("dgn"))
        //            await PostJob(OCID, urlj, 4);
        //        if (extension.Equals("dwg") || extension.Equals("dxf")) // || extension.ToLower().Equals(".dxf") || extension.ToLower().Equals(".dgn"))
        //            await PostJob(OCID, urlj, 5);
        //        //fetch textdata, xdata & thumbnail into cache from OC server...not mandatory////
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "textdata");
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "xdata");
        //        if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
        //            fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "thumbnail.bmp");
        //        string OCStr = OCID + "|" + name;
        //        File.WriteAllText(OCPath, OCStr, Encoding.UTF8);
        //        _logger.LogInfo($"OC Conversion successful for {filepath}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //        throw;
        //    }
        //}

        //private async Task FetchOCFiles(ProcessingJob job)
        //{
        //    var ext = System.IO.Path.GetExtension(job.FilePath).Replace(".","").ToLower();
        //    //fetch textdata, xdata & thumbnail into cache from OC server...not mandatory////
        //    if (ext.Equals("dwg") || ext.Equals("dxf"))
        //        fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "textdata");
        //    if (ext.Equals("dwg") || ext.Equals("dxf"))
        //        fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "xdata");
        //    if (ext.Equals("dwg") || ext.Equals("dxf"))
        //        fetchOCFiles(job.FilePath, job.CacheFolder, job.OCID, _paths.TokenNumber, "thumbnail.bmp");
        //}

        //private async void fetchOCFilesDep(string filepath, string cacheFolder, string OCID, string token, string fnam)
        //{
        //    try
        //    {
        //        string destinationPath = "";
        //        if (fnam.Equals("textdata"))
        //        {
        //            Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Txt"));
        //            destinationPath = System.IO.Path.Combine(cacheFolder, "_Txt", "1");
        //        }
        //        else if (fnam.Equals("xdata"))
        //            destinationPath = System.IO.Path.Combine(cacheFolder, "xdata");
        //        else if (fnam.Equals("thumbnail.bmp"))
        //            destinationPath = System.IO.Path.Combine(cacheFolder, "_Thm", "1.png");
        //        if (!fnam.Equals("thumbnail.bmp"))
        //        {
        //            string jsontext = "Status Code: ";
        //            int counter = 1;
        //            while (jsontext.Substring(0, 1).Equals("S") && counter < 11)
        //            {
        //                jsontext = await getOCFiles(OCID, _paths.TokenNumber, fnam);
        //                await Task.Delay(3000);
        //                if (counter == 10)
        //                    _logger.LogInfo($"Exceeded 10 attempts..\nUnable to fetch {fnam} at this moment...");
        //                counter++;
        //            }

        //            if (!jsontext.Substring(0, 1).Equals("S"))
        //            {
        //                if (fnam.Equals("textdata"))
        //                {
        //                    Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Txt"));
        //                }
        //                File.WriteAllText(destinationPath, jsontext);
        //                _logger.LogInfo($"Successfully fetched job file: {fnam}");
        //            }
        //        }
        //        else
        //        {
        //            _logger.LogInfo("Into OCFilesBinary for CAD thumbnail creation");
        //            int counter = 0;
        //            while (counter <= 50)
        //            {
        //                counter++;
        //                responseObj obj = await getOCFilesBinary(OCID, _paths.TokenNumber, fnam);
        //                if (obj.content != null)
        //                {
        //                    _logger.LogInfo($"Thumbnail fetched successfully: {obj.responseStatus}");
        //                        Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Thm"));
        //                    File.WriteAllBytes(destinationPath, obj.content);
        //                    break;
        //                }
        //                if (counter == 50)
        //                {
        //                    _logger.LogInfo($"Unable to fetch thumbnail: {obj.responseStatus}");
        //                }
        //                await Task.Delay(1000);

        //            }
        //        }

        //    }
        //    catch (Exception ex)
        //    {
        //       _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }
        //}

        //private void GeneratePNGThumbnail(MagickImage image, string cacheFolderPath, int? frameIndex)
        //{
        //    try
        //    {
        //        // Configure thumbnail settings
        //        ConfigureThumbnailSettings(image);

        //        // Convert to PNG
        //        image.Format = MagickFormat.Png;

        //        // Generate output key for thumbnail
        //        string thumbnailKey = GenerateThumbnailOutputKey(cacheFolderPath, frameIndex);
        //        //Save to file
        //        image.Write(thumbnailKey, MagickFormat.Png);

        //        _logger.LogInfo($"Successfully uploaded thumbnail: {thumbnailKey}");
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogErrorEx(ex, $"Error: {ex.Message}");
        //    }

        //}

        //private void ConfigureThumbnailSettings(IMagickImage image)
        //{
        //    // Set thumbnail size (adjust dimensions as needed)
        //    image.Resize(new MagickGeometry
        //    {
        //        Width = 300,  // thumbnail width
        //        Height = 300, // thumbnail height
        //        IgnoreAspectRatio = false
        //    });

        //    // Optional: Enhance thumbnail quality
        //    image.Quality = 85;
        //    image.Sharpen(0, 1.0); // Slight sharpening for better thumbnail clarity

        //    // Ensure color settings
        //    image.ColorType = ImageMagick.ColorType.TrueColorAlpha;
        //    image.ColorSpace = ColorSpace.RGB;
        //    image.Alpha(AlphaOption.Set);
        //    image.Strip(); // Removes any profiles or comments
        //    image.Quality = 90;
        //}

        //private void ConfigureImageSettings(IMagickImage image)
        //{
        //    // Basic settings for all images
        //    image.ColorSpace = ColorSpace.RGB;
        //    image.Alpha(AlphaOption.Set);
        //    image.ColorType = ImageMagick.ColorType.TrueColorAlpha;

        //    // WebP specific settings
        //    var webpSettings = new WebPWriteDefines
        //    {
        //        Method = 6, // 0=fastest, 6=best quality
        //        Lossless = true,
        //        //EntropyAnalysis = true,
        //        ThreadLevel = true,
        //        Preprocessing = WebPPreprocessing.SegmentSmooth, // Level of pre-processing
        //        FilterStrength = 60,
        //        FilterSharpness = 0,
        //        FilterType = WebPFilterType.Simple, //Strong
        //        AutoFilter = true,
        //        AlphaCompression = WebPAlphaCompression.Compressed,
        //        AlphaFiltering = WebPAlphaFiltering.Best,   //Fast, None
        //        AlphaQuality = 90,
        //        Pass = 1 // Number of passes
        //    };
        //    using var magickImage = new MagickImage(image.ToByteArray());
        //    magickImage.Settings.SetDefines(webpSettings);


        //    // Quality settings based on image type
        //    if (image.Format == MagickFormat.Jpeg || image.Format == MagickFormat.Jpg)
        //    {
        //        image.Quality = 85;
        //    }
        //    else if (image.Format == MagickFormat.Png || image.Format == MagickFormat.Tiff)
        //    {
        //        image.Quality = 90;
        //    }
        //    else
        //    {
        //        image.Quality = 85; // Default quality
        //    }

        //    // Optional: Resize if image is too large
        //    if (image.Width > 4096 || image.Height > 4096)
        //    {
        //        image.Resize(new MagickGeometry
        //        {
        //            Width = 4096,
        //            Height = 4096,
        //            IgnoreAspectRatio = false
        //        });
        //    }
        //}

        //private void ConfigureImageSettings(IMagickImage image)
        //{
        //    // ✅ STEP 1: Resize FIRST (biggest win)
        //    if (image.Width > 4096 || image.Height > 4096)
        //    {
        //        image.Resize(new MagickGeometry
        //        {
        //            Width = 4096,
        //            Height = 4096,
        //            IgnoreAspectRatio = false
        //        });
        //    }

        //    // ✅ STEP 2: Apply expensive pixel operations AFTER resize
        //    image.ColorSpace = ColorSpace.RGB;
        //    image.Alpha(AlphaOption.Set);
        //    image.ColorType = ImageMagick.ColorType.TrueColorAlpha;

        //    // ✅ STEP 3: WebP settings
        //    var webpSettings = new WebPWriteDefines
        //    {
        //        Method = 4,
        //        Lossless = false,
        //        AutoFilter = true,
        //        AlphaQuality = 85
        //    };

        //    if (image is MagickImage magickImage)
        //    {
        //        magickImage.Settings.SetDefines(webpSettings);
        //    }

        //    // ✅ Set quality here (correct place)
        //    image.Quality = 85;
        //}


        
 
 */
