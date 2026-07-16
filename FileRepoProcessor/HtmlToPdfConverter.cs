using SkiaSharp;
using Spire.Additions.Chrome;
using Spire.Additions.Qt;
using Spire.Pdf.Graphics;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public class HtmlToPdfConverter : IHtmlToPdfConverter
    {
        private readonly BrowserInfo _browserInfo;
        private readonly ILogger<HtmlToPdfConverter> _logger;

        
        public HtmlToPdfConverter(
            BrowserInfo browserInfo,
            ILogger<HtmlToPdfConverter> logger)
            
        {
            _browserInfo = browserInfo;
            _logger = logger;
            
        }

    
        public Stream Convert(HtmlToPdfRequest request)
        {
            if (request.HasHtmlPath && !File.Exists(request.HtmlPath))
            {
                throw new FileNotFoundException(
                    $"HTML file not found: {request.HtmlPath}");
            }

            if (!request.HasHtmlPath && !request.HasHtmlStream)
            {
                throw new InvalidOperationException(
                    "No HTML source was supplied.");
            }
            var engine = _browserInfo.Type == BrowserType.None ? "Qt"
                                : _browserInfo.Type.ToString();
            _logger.LogDebug("Using {Engine} HTML renderer.", engine);
            if (_browserInfo.HasChromiumBrowser && _browserInfo.isUsable)
            {
                try
                {
                    _logger.LogDebug("Using Chrome HTML renderer.");
                    return ConvertUsingChrome(request);
                }
                catch (Exception ex)
                {
                    //Not really necessary but keeping it as a second fallback 
                    _logger.LogWarning(ex,
                        "Chrome conversion failed. Falling back to Qt.");
                }

            }
            _logger.LogDebug("Using Qt HTML renderer.");
            return ConvertUsingQt(request);
        }

        private Stream ConvertUsingChrome(HtmlToPdfRequest request)
        {
            string? tempHtmlPath = null;
            try
            {
                if (!request.HasHtmlPath && !request.HasHtmlStream)
                    throw new Exception($"Html source not available for conversion");
                MemoryStream stream = new MemoryStream();
                string url = string.Empty;
                if (request.HasHtmlPath)
                {
                    url = new Uri(request.HtmlPath).AbsoluteUri;
                }
                else
                {
                    tempHtmlPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.html");
                    using (var fileStream = File.Create(tempHtmlPath))
                    {
                        if (request.HtmlStream.CanSeek)
                            request.HtmlStream.Position = 0;
                        request.HtmlStream.CopyTo(fileStream);
                        if (request.HtmlStream.CanSeek)
                            request.HtmlStream.Position = 0;
                    }
                    url = new Uri(tempHtmlPath).AbsoluteUri;
                }

                //Specify the path to the Chrome plugin
                string chromeLocation = _browserInfo.ExecutablePath;

                //Create an instance of the ChromeHtmlConverter class
                using var converter = new ChromeHtmlConverter(chromeLocation);
                // Create an instance of the ConvertOptions class
                ConvertOptions options = new ConvertOptions();
                //Set conversion timeout
                options.Timeout = request.Timeout;
                options.PaperFormat = Spire.Additions.Chrome.Enums.PaperFormat.FitPageToContent;
                //Set paper size and page margins of the converted PDF
                options.PageSettings = new PageSettings()
                {
                    //PaperWidth = request.PageSize.Width,
                    //PaperHeight = request.PageSize.Height,
                    //MarginTop = request.Margins.Top,
                    //MarginLeft = request.Margins.Left,
                    //MarginRight = request.Margins.Right,
                    //MarginBottom = request.Margins.Bottom,

                };

                //Convert the URL to PDF
                converter.ConvertToPdf(url, stream, options);
                if (stream.Length == 0)
                {
                    throw new InvalidOperationException(
                        "PDF conversion produced an empty stream.");
                }
                stream.Position = 0;
                return stream;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(tempHtmlPath) &&
                        File.Exists(tempHtmlPath))
                    {
                        File.Delete(tempHtmlPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Unable to delete temporary HTML file: {File}",
                        tempHtmlPath);
                }
            }
        }

        private Stream ConvertUsingQt(HtmlToPdfRequest request)
        {
            //Not necessary for Qt conversion as it is called from convert where the 
            //check is already done...keeping it in case needed for future enhancement
            if (!request.HasHtmlPath && !request.HasHtmlStream)
                throw new Exception($"Html source not available for conversion");
            MemoryStream stream = new MemoryStream();
            string htmlContents = string.Empty;
            if (request.HasHtmlPath)
            {
                htmlContents = File.ReadAllText(request.HtmlPath);
            }
            else
            {
                htmlContents = GetHtmlContent(request.HtmlStream);
            }
            HtmlConverter.Convert(htmlContents,
                                     stream,
                                     request.EnableJavaScript,
                                     request.Timeout,
                                     request.PageSize,
                                     request.Margins,
                                     LoadHtmlType.SourceCode);
            if (stream.Length == 0)
            {
                throw new InvalidOperationException(
                    "PDF conversion produced an empty stream.");
            }
            stream.Position = 0;
            return stream;
        }

        private static string GetHtmlContent(Stream htmlStream)
        {
            if (htmlStream.CanSeek)
                htmlStream.Position = 0;

            using var reader = new StreamReader(
                htmlStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);

            string html = reader.ReadToEnd();

            if (htmlStream.CanSeek)
                htmlStream.Position = 0;

            return html;
        }

        public bool ValidateChrome()
        {
            string tempHtml = Path.Combine(
                Path.GetTempPath(),
                $"{Guid.NewGuid()}.html");

            File.WriteAllText(
                tempHtml,
                "<html><body>Validation</body></html>");

            try
            {
                var request = new HtmlToPdfRequest
                {
                    HtmlPath = tempHtml,
                    PageSize = new SizeF(300, 300),
                    Margins = new PdfMargins(0)
                };

                using Stream pdf = ConvertUsingChrome(request);
                return pdf.Length > 0;
                
            }
            finally
            {
                if (File.Exists(tempHtml))
                    File.Delete(tempHtml);
            }
        }
    }

    public class HtmlToPdfRequest
    {
        public string? HtmlPath { get; set; } 

        public Stream? HtmlStream { get; set; }
        
        public SizeF PageSize { get; set; }

        public PdfMargins Margins { get; set; }

        public bool EnableJavaScript { get; set; } = true;

        public int Timeout { get; set; } = 100000;
        // Convenience properties
        public bool HasHtmlPath =>
            !string.IsNullOrWhiteSpace(HtmlPath);

        public bool HasHtmlStream =>
            HtmlStream != null && HtmlStream != Stream.Null;
    }

    //public class HtmlRendererState
    //{
    //    public bool UseChrome { get; set; }

    //    public string? ValidationError { get; set; }
    //}
}
