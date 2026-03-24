using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
using System.Runtime;
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
using Spire.Additions.Xps.Schema;
using Spire.Doc;
using Spire.Doc.Interface;
using Spire.Pdf;
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
        public FileProcessor(ILogger<FileProcessor> logger, IPathMapper mapper, IOptions<PathOptions> options)
        {
            _mapper = mapper;
            _logger = logger;
            _paths = options.Value;
        }

        public bool IsProcessed(string repoFile)
        {
            var flag = System.IO.Path.Combine(
                _mapper.GetCacheFolder(repoFile),
                "_c");

            return File.Exists(flag);
        }

        public async Task ProcessAsync(string repoFile, CancellationToken token)
        {
            FileLockHelper.Wait(repoFile);

            var cacheFolder = _mapper.GetCacheFolder(repoFile);
            Directory.CreateDirectory(cacheFolder);

            // 🔧 actual processing logic here
            string ext = System.IO.Path.GetExtension(repoFile).ToLower();
            switch (ext)
            {
                case ".doc":
                case ".docx":
                    ProcessWord(repoFile, cacheFolder);
                    break;

                case ".xls":
                case ".xlsx":
                    ProcessExcel(repoFile, cacheFolder);
                    break;

                case ".ppt":
                case ".pptx":
                    ProcessPowerPoint(repoFile, cacheFolder);
                    break;
                case ".pdf":
                    ProcessPDF(repoFile, cacheFolder);
                    break;
                case ".dwg":
                case ".dxf":
                case ".dgn":
                case ".ifc":
                case ".obj":
                case ".stl":
                case ".stp":
                    ProcessCAD(repoFile, cacheFolder);
                    break;
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".tif":
                case ".tiff":
                case ".bmp":
                case ".webp":
                    ProcessRaster(repoFile, cacheFolder);
                    break;
                default:
                    break;
            }

            await Task.Delay(500, token);

            File.WriteAllText(System.IO.Path.Combine(cacheFolder, "_c"), "");
        }

        private void ProcessWord(string filepath, string cacheFolder)
        {
            try
            {
                Spire.Doc.Document doc = new Spire.Doc.Document();
                doc.LoadFromFile(filepath);
                int count = WordToSVGConverter(filepath, cacheFolder, doc);
                WordToThmConverter(filepath, cacheFolder, doc, count);
                string indexPath = System.IO.Path.Combine(cacheFolder, "index.json");
                string FName = System.IO.Path.GetFileName(filepath);
                saveIndexFile(indexPath, doc, FName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
                doc.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }

        private int WordToSVGConverter(string source, string dest, Spire.Doc.Document doc)
        {
            try
            {
                Queue<byte[]> svgBytes = doc.SaveToSVG();
                int len = svgBytes.Count;
                string completePath = System.IO.Path.Combine(dest, "_PGC");
                File.WriteAllText(completePath, len.ToString());
                Directory.CreateDirectory(System.IO.Path.Combine(dest, "_S"));
                for (int i = 0; i < len; i++)
                {
                    FileStream fs = new FileStream(System.IO.Path.Combine(dest, "_S", string.Format("{0}", i + 1)), FileMode.Create);
                    byte[] bytes = svgBytes.Dequeue();
                    fs.Write(bytes, 0, bytes.Length);

                }
                _logger.LogInfo($"SVG conversion successful for {source}");
                return len;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }

        }

        private void WordToThmConverter(string source, string dest, Spire.Doc.Document doc, int count)
        {
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Stream[] imges = doc.SaveImageToStreams(i, 1, 0);

                    if (imges[0] != null)
                    {
                        byte[] byteStr = ResizeImage(imges[0], 150, 150);
                        Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
                        File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
                    }
                }
                _logger.LogInfo($"Thumbnail conversion successful for {source}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
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


        private void saveIndexFile(string indexPath, Spire.Doc.Document doc, string FName)
        {
            try
            {
                int totalpage = doc.PageCount; // svgBytes.Count;
                Indexdetails[] objIdetailsarr = new Indexdetails[totalpage];
                for (int i = 0; i < totalpage; i++)
                {

                    Indexdetails objindexdetails = new Indexdetails();
                    objindexdetails.page = i + 1;
                    objindexdetails.Width = doc.Sections[0].PageSetup.PageSize.Width.ToString();
                    objindexdetails.Height = doc.Sections[0].PageSetup.PageSize.Height.ToString();//interchange ? pagesize.Width.ToString() : pagesize.Height.ToString(); //decHeight.ToString();
                    objIdetailsarr[i] = objindexdetails;
                }
                string json = JsonConvert.SerializeObject(objIdetailsarr);
                string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
                File.WriteAllText(indexPath, jsonstr); //  await awsLib.writeAllTextToS3Async(C, BName, indexPath, jsonstr);
                _logger.LogInfo($"Index file creation successful at {indexPath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
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
                //Get the first worksheet
                int count = workbook.Worksheets.Count;
                ExcelToSVGConverter(filepath, cacheFolder, workbook);
                ExcelToThmConverter(filepath, cacheFolder, workbook);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
                workbook.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }

        private void ExcelToSVGConverter(string filepath, string cacheFolder, Workbook workbook)
        {
            try
            {
                Indexdetails[] objIdetailsarr = new Indexdetails[workbook.Worksheets.Count];
                for (int i = 0; i < workbook.Worksheets.Count; i++)
                {

                    Worksheet sheet = workbook.Worksheets[i];
                    using (var ms = new MemoryStream())
                    {
                        sheet.ToSVGStream(ms, 0, 0, 0, 0);
                        ms.Position = 0;
                        // Parse SVG size here
                        var svgDoc = XDocument.Load(ms);
                        var svg = svgDoc.Root;
                        Indexdetails objindexdetails = new Indexdetails();
                        objindexdetails.page = i + 1;
                        var viewBox = svg.Attribute("viewBox")?.Value;
                        if (!string.IsNullOrEmpty(viewBox))
                        {
                            var vb = viewBox.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            objindexdetails.Width = double.Parse(vb[2], CultureInfo.InvariantCulture).ToString();
                            objindexdetails.Height = double.Parse(vb[3], CultureInfo.InvariantCulture).ToString();
                        }
                        else
                        {
                            objindexdetails.Width = ParseSvgLength(svg.Attribute("width")?.Value).ToString();
                            objindexdetails.Height = ParseSvgLength(svg.Attribute("height")?.Value).ToString();
                        }

                        objIdetailsarr[i] = objindexdetails;
                        // OPTIONAL: now write to file if needed
                        File.WriteAllBytes(
                            System.IO.Path.Combine(cacheFolder, "_S", $"{i + 1}"),
                            ms.ToArray());
                    }
                }
                string json = JsonConvert.SerializeObject(objIdetailsarr);
                string jsonstr = "{\"File\":\"" + "SampleExcle" + "\"" + "," + "\"Pages\":" + json + "}";
                File.WriteAllText(System.IO.Path.Combine(cacheFolder, "index.json"), jsonstr);
                _logger.LogInfo($"SVG and Index file creation successful for {filepath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        static int ParseSvgLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            value = value.Trim().ToLowerInvariant();

            if (value.EndsWith("px"))
                value = value[..^2];

            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        private void ExcelToThmConverter(string source, string dest, Workbook doc)
        {
            try
            {
                int count = doc.Worksheets.Count;
                for (int i = 0; i < count; i++)
                {

                    Worksheet sheet = doc.Worksheets[i];
                    // Render worksheet to Image
                    Stream stream = sheet.ToImage(0, 0, 0, 0);

                    if (stream != null)
                    {
                        byte[] byteStr = ResizeImage(stream, 150, 150);
                        Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
                        File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
                    }
                }
                _logger.LogInfo($"Thumbnail conversion successful for {source}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }

        }
        private void ProcessPowerPoint(string filepath, string cacheFolder)
        {
            try
            {
                Presentation ppt = new Presentation();
                ppt.LoadFromFile(filepath);
                string FName = System.IO.Path.GetFileName(filepath);
                string indexPath = System.IO.Path.Combine(cacheFolder, "index.json");
                int count = PptToSVGConverter(filepath, cacheFolder, ppt);
                PptToThmConverter(filepath, cacheFolder, ppt);
                saveIndexFile(indexPath, ppt, FName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
                ppt.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }
        private int PptToSVGConverter(string source, string dest, Presentation ppt)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(dest, "_S"));
                Queue<byte[]> svgBytes = ppt.SaveToSVG();
                int len = svgBytes.Count;
                string CompletePath = System.IO.Path.Combine(dest, "_PGC");
                File.WriteAllText(CompletePath, len.ToString());
                for (int i = 0; i < len; i++)
                {
                    FileStream fs = new FileStream(System.IO.Path.Combine(dest, "_S", string.Format("{0}", i + 1)), FileMode.Create);
                    byte[] bytes = svgBytes.Dequeue();
                    fs.Write(bytes, 0, bytes.Length);
                }
                return len;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }
        public static void saveIndexFile(string indexPath, Presentation doc, string FName)
        {
            try
            {
                int totalpage = doc.Slides.Count;
                string width = ((int)Math.Round(doc.SlideSize.Size.Width, MidpointRounding.AwayFromZero)).ToString();
                string height = ((int)Math.Round(doc.SlideSize.Size.Height, MidpointRounding.AwayFromZero)).ToString();
                Indexdetails[] objIdetailsarr = new Indexdetails[totalpage];
                for (int i = 0; i < totalpage; i++)
                {

                    Indexdetails objindexdetails = new Indexdetails();
                    objindexdetails.page = i + 1;
                    objindexdetails.Width = width;
                    objindexdetails.Height = height;//interchange ? pagesize.Width.ToString() : pagesize.Height.ToString(); //decHeight.ToString();
                    objIdetailsarr[i] = objindexdetails;
                }
                string json = JsonConvert.SerializeObject(objIdetailsarr);
                string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
                File.WriteAllText(indexPath, jsonstr); //  await awsLib.writeAllTextToS3Async(C, BName, indexPath, jsonstr);

            }
            catch (Exception ex)
            {

            }
        }

        public static void PptToThmConverter(string source, string dest, Presentation doc)
        {
            
            int count = doc.Slides.Count;
            for (int i = 0; i < count; i++)
            {
                ISlide slide = doc.Slides[i];
                // Render slide to Image
                Stream image = slide.SaveAsImage();
                
                if (image != null)
                {
                    byte[] byteStr = ResizeImage(image, 150, 150);
                    Directory.CreateDirectory(System.IO.Path.Combine(dest, "_Thm"));
                    File.WriteAllBytes(System.IO.Path.Combine(dest, "_Thm", String.Format("{0}.png", (i + 1))), byteStr);
                }
            }

        }

        private void ProcessPDF(string filepath, string cacheFolder)
        {
            try
            {
                PdfDocument pdfDocument = new PdfDocument(filepath);
                int count = pdfDocument.Pages.Count;
                PdfToSVGConverter(filepath, cacheFolder, pdfDocument);
                PdfToThmConverter(filepath, cacheFolder, pdfDocument, count);
                string indexPath = System.IO.Path.Combine(cacheFolder, "index.json");
                string FName = System.IO.Path.GetFileName(filepath);
                saveIndexFile(indexPath, pdfDocument, FName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
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

        private async void ProcessCAD(string filepath, string cacheFolder)
        {
            try
            {
                string xPath = System.IO.Path.Combine(cacheFolder, "_x.txt");
                bool is_xFile = File.Exists(xPath); // await awsLib.DoesS3KeyExistAsync(S3Client, sourceBucket, sourcePath.xPath);
                if (is_xFile)
                {
                    _logger.LogInfo($"Key {xPath} Exists.");
                    string xpathStr = File.ReadAllText(xPath);
                    if (string.IsNullOrEmpty(xpathStr))
                    {
                        //treat as normal CAD file.
                        _logger.LogInfo($"Key {xPath} exists with no content. Proceeding normally ");
                        await OCConvert(filepath, cacheFolder);
                        string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                        saveTFile(filepath, TPath, 1);
                    }
                    else
                    {
                        string[] XRefPaths = xpathStr.Split('|');
                        await OCConvert(filepath, cacheFolder, XRefPaths);
                        string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                        saveTFile(filepath, TPath, 1);

                    }
                }
                else
                {
                    //treat as normal CAD file.
                    _logger.LogInfo($"Key {xPath} does not exist. Processing normally ");
                    await OCConvert(filepath, cacheFolder);
                    string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                    saveTFile(filepath, TPath, 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error in ConvertCADAsync: {ex.StackTrace}");
                throw;
            }
        }

        public async Task OCConvert(string filepath, string cacheFolder)
        {
            try
            {
                _logger.LogInfo($"Into OCConver Function");
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
                ///////////////////////////////////////////////////////
                //fetch textdata, xdata & thumbnail into cache from OC server...not mandatory////
                bool textdataRes = false, xdataRes = false, thumbRes = false;
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf") || extension.ToLower().Equals("dgn"))
                {
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "textdata");
                }

                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                {
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "xdata");
                }

                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                {
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "thumbnail.bmp");
                }
                
                
                string OCStr = OCID + "|" + name;
                string OCPath = System.IO.Path.Combine(cacheFolder,"_OC");
                File.WriteAllText(OCPath, OCStr); 
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }

        public async Task OCConvert(string filepath, string cacheFolder, string[] XRefPaths)
        {
            try
            {
                var extension = System.IO.Path.GetExtension(filepath).Replace(".","").ToLower();
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
                //fetch textdata, xdata & thumbnail into cache from OC server...not mandatory////
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "textdata");
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "xdata");
                if (extension.ToLower().Equals("dwg") || extension.ToLower().Equals("dxf"))
                    fetchOCFiles(filepath, cacheFolder, OCID, _paths.TokenNumber, "thumbnail.bmp");
                string OCStr = OCID + "|" + name;
                File.WriteAllText(OCPath, OCStr, Encoding.UTF8);
                _logger.LogInfo($"OC Conversion successful for {filepath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
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

        private async void fetchOCFiles(string filepath, string cacheFolder, string OCID, string token, string fnam)
        {
            try
            {
                string destinationPath = "";
                if (fnam.Equals("textdata"))
                {
                    Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Txt"));
                    destinationPath = System.IO.Path.Combine(cacheFolder, "_Txt", "1");
                }
                else if (fnam.Equals("xdata"))
                    destinationPath = System.IO.Path.Combine(cacheFolder, "xdata");
                else if (fnam.Equals("thumbnail.bmp"))
                    destinationPath = System.IO.Path.Combine(cacheFolder, "_Thm", "1.png");
                if (!fnam.Equals("thumbnail.bmp"))
                {
                    string jsontext = "Status Code: ";
                    int counter = 1;
                    while (jsontext.Substring(0, 1).Equals("S") && counter < 11)
                    {
                        jsontext = await getOCFiles(OCID, _paths.TokenNumber, fnam);
                        await Task.Delay(3000);
                        if (counter == 10)
                            _logger.LogInfo($"Exceeded 10 attempts..\nUnable to fetch {fnam} at this moment...");
                        counter++;
                    }

                    if (!jsontext.Substring(0, 1).Equals("S"))
                    {
                        if (fnam.Equals("textdata"))
                        {
                            Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Txt"));
                        }
                        File.WriteAllText(destinationPath, jsontext);
                        _logger.LogInfo($"Successfully fetched job file: {fnam}");
                    }
                }
                else
                {
                    _logger.LogInfo("Into OCFilesBinary for CAD thumbnail creation");
                    int counter = 0;
                    while (counter <= 50)
                    {
                        counter++;
                        responseObj obj = await getOCFilesBinary(OCID, _paths.TokenNumber, fnam);
                        if (obj.content != null)
                        {
                            _logger.LogInfo($"Thumbnail fetched successfully: {obj.responseStatus}");
                                Directory.CreateDirectory(System.IO.Path.Combine(cacheFolder, "_Thm"));
                            File.WriteAllBytes(destinationPath, obj.content);
                            break;
                        }
                        if (counter == 50)
                        {
                            _logger.LogInfo($"Unable to fetch thumbnail: {obj.responseStatus}");
                        }
                        await Task.Delay(1000);

                    }
                }

            }
            catch (Exception ex)
            {
               _logger.LogErrorEx(ex, $"Error: {ex.Message}");
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
                var fName = System.IO.Path.GetFileName(filepath);
                byte[] fileBytes = File.ReadAllBytes(filepath);
                var inputStream  = new MemoryStream(fileBytes);
                inputStream.Position = 0;
                var extension = System.IO.Path.GetExtension(filepath).Replace(".", "").ToLower();
                if (IsMultiFrameFormat(extension))
                {
                    ProcessMultiFrameImage(inputStream, filepath, cacheFolder);
                }
                else
                {
                    ProcessSingleFrameImage(inputStream, filepath, cacheFolder);
                }
                inputStream.Position = 0;
                var indexPath = System.IO.Path.Combine(cacheFolder, "index.json");
                var count = saveIndexFile(indexPath, inputStream, fName);
                string TPath = System.IO.Path.Combine(cacheFolder, "_T");
                saveTFile(filepath, TPath, count);
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }

        private bool IsMultiFrameFormat(string extension)
        {
            return extension.ToLower() == "gif" || extension.ToLower() == "tiff" || extension.ToLower() == "tif";
        }

        private int saveIndexFile(string indexPath, MemoryStream inputstream, string FName)
        {
            try
            {
                using var images = new MagickImageCollection(inputstream);

                Indexdetails[] objIdetailsarr = new Indexdetails[images.Count];
                for (int i = 0; i < images.Count; i++)
                {
                    Indexdetails objindexdetails = new Indexdetails();
                    objindexdetails.page = i + 1;
                    objindexdetails.Width = images[i].Width.ToString(); // decWidth.ToString();
                    objindexdetails.Height = images[i].Height.ToString(); //decHeight.ToString();
                    objindexdetails.angle = 0;
                    objIdetailsarr[i] = objindexdetails;
                }
                string json = JsonConvert.SerializeObject(objIdetailsarr);
                string jsonstr = "{\"File\":\"" + FName + "\"" + "," + "\"Pages\":" + json + "}";
                File.WriteAllText(indexPath, jsonstr);
                return images.Count;
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
                throw;
            }
        }

        private void ProcessMultiFrameImage(MemoryStream inputStream, string filepath, string cacheFolder)
        {
            try
            {
                using var images = new MagickImageCollection(inputStream);
                _logger.LogInfo($"Processing multi-frame image with {images.Count} frames");

                for (int i = 0; i < images.Count; i++)
                {
                    using var frame = images[i];
                    ProcessAndUploadFrame(frame, cacheFolder, i);
                }
                _logger.LogInfo($"Successfully processed multi-frame image and thumbnail with {images.Count} frames for {filepath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Exception encountered: {ex.Message}");
                throw;
            }
        }

        private void ProcessSingleFrameImage(MemoryStream inputStream, string filepath, string cacheFolder)
        {
            try
            {
                using var image = new MagickImage(inputStream);
                ProcessAndUploadFrame(image, cacheFolder, null);
                _logger.LogInfo($"Successfully processed single-frame image and thumbnail with 1 frame for {filepath}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Exception encountered: {ex.Message}");
                throw;
            }
        }

        private void ProcessAndUploadFrame(IMagickImage image, string cacheFolderPath, int? frameIndex)
        {
            try
            {
                // Create a clone for thumbnail to avoid modifying original image
                using var thumbnailImage = new MagickImage(image.ToByteArray());
                GenerateWebPBase64(image, cacheFolderPath, frameIndex);
                GeneratePNGThumbnail(thumbnailImage, cacheFolderPath, frameIndex);

            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error processing frame {frameIndex}: {ex.Message}");
                throw;
            }
        }

        private void GenerateWebPBase64(IMagickImage image, string cacheFolder, int? frameIndex)
        {
            try
            {
                // Configure image settings
                ConfigureImageSettings(image);

                // Convert to WebP
                image.Format = MagickFormat.WebP;

                // Generate output path for base64
                string outputKey = GenerateBase64OutputKey(cacheFolder, frameIndex);

                // Convert to base64
                using var outputStream = new MemoryStream();
                image.Write(outputStream, MagickFormat.WebP);
                outputStream.Position = 0;

                byte[] imageBytes = outputStream.ToArray();
                string base64String = Convert.ToBase64String(imageBytes);

                File.WriteAllText(outputKey, base64String);

                _logger.LogInfo($"Successfully uploaded base64 string: {outputKey}");
            }
            catch(Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }
        }

        private void GeneratePNGThumbnail(IMagickImage image, string cacheFolderPath, int? frameIndex)
        {
            try
            {
                // Configure thumbnail settings
                ConfigureThumbnailSettings(image);

                // Convert to PNG
                image.Format = MagickFormat.Png;

                // Generate output key for thumbnail
                string thumbnailKey = GenerateThumbnailOutputKey(cacheFolderPath, frameIndex);
                //Save to file
                image.Write(thumbnailKey, MagickFormat.Png);

                _logger.LogInfo($"Successfully uploaded thumbnail: {thumbnailKey}");
            }
            catch (Exception ex)
            {
                _logger.LogErrorEx(ex, $"Error: {ex.Message}");
            }

        }

        private string GenerateThumbnailOutputKey(string cacheFolderPath, int? frameIndex)
        {
            if (frameIndex.HasValue)
            {
                return System.IO.Path.Combine(cacheFolderPath, "_Thm", string.Format("{0}.png", frameIndex + 1)); 
            }
            return System.IO.Path.Combine(cacheFolderPath, "_Thm", "1");
        }
        private void ConfigureThumbnailSettings(IMagickImage image)
        {
            // Set thumbnail size (adjust dimensions as needed)
            image.Resize(new MagickGeometry
            {
                Width = 300,  // thumbnail width
                Height = 300, // thumbnail height
                IgnoreAspectRatio = false
            });

            // Optional: Enhance thumbnail quality
            image.Quality = 85;
            image.Sharpen(0, 1.0); // Slight sharpening for better thumbnail clarity

            // Ensure color settings
            image.ColorType = ImageMagick.ColorType.TrueColorAlpha;
            image.ColorSpace = ColorSpace.RGB;
            image.Alpha(AlphaOption.Set);
            image.Strip(); // Removes any profiles or comments
            image.Quality = 90;
        }

        private void ConfigureImageSettings(IMagickImage image)
        {
            // Basic settings for all images
            image.ColorSpace = ColorSpace.RGB;
            image.Alpha(AlphaOption.Set);
            image.ColorType = ImageMagick.ColorType.TrueColorAlpha;

            // WebP specific settings
            var webpSettings = new WebPWriteDefines
            {
                Method = 6, // 0=fastest, 6=best quality
                Lossless = true,
                //EntropyAnalysis = true,
                ThreadLevel = true,
                Preprocessing = WebPPreprocessing.SegmentSmooth, // Level of pre-processing
                FilterStrength = 60,
                FilterSharpness = 0,
                FilterType = WebPFilterType.Simple, //Strong
                AutoFilter = true,
                AlphaCompression = WebPAlphaCompression.Compressed,
                AlphaFiltering = WebPAlphaFiltering.Best,   //Fast, None
                AlphaQuality = 90,
                Pass = 1 // Number of passes
            };
            using var magickImage = new MagickImage(image.ToByteArray());
            magickImage.Settings.SetDefines(webpSettings);


            // Quality settings based on image type
            if (image.Format == MagickFormat.Jpeg || image.Format == MagickFormat.Jpg)
            {
                image.Quality = 85;
            }
            else if (image.Format == MagickFormat.Png || image.Format == MagickFormat.Tiff)
            {
                image.Quality = 90;
            }
            else
            {
                image.Quality = 85; // Default quality
            }

            // Optional: Resize if image is too large
            if (image.Width > 4096 || image.Height > 4096)
            {
                image.Resize(new MagickGeometry
                {
                    Width = 4096,
                    Height = 4096,
                    IgnoreAspectRatio = false
                });
            }
        }
        private string GenerateBase64OutputKey(string cacheFolder, int? frameIndex)
        {
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
