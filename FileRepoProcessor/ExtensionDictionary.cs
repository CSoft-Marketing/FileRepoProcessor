using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileRepoProcessor
{
    public static class SupportedExtensions
    {
        public static readonly HashSet<string> Word =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".doc",
                ".docx",
                ".rtf",
                ".odt",
                ".dot",
                ".dotx"
            };

        public static readonly HashSet<string> Excel =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".xls",
                ".xlsx",
                ".ods"
            };

        public static readonly HashSet<string> Powerpoint =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".ppt",
                ".pptx",
                ".pps",
                ".ppsx",
                ".pot",
                ".potx"
            };
        public static readonly HashSet<string> Pdf =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".pdf"
            };
        public static readonly HashSet<string> Cad =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".dwg",
                ".dxf",
                ".dgn",
                ".ifc",
                ".stl",
                ".obj",
                ".stp"
            };
        public static readonly HashSet<string> Raster =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg",
                ".jpeg",
                ".tif",
                ".tiff",
                ".gif",
                ".png",
                ".bmp",
                ".webp"
            };
        public static readonly HashSet<string> Html =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".htm",
                ".html",
                ".xhtml"
            };
        public static readonly HashSet<string> Email =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".eml",
                ".msg",
            };
        public static readonly HashSet<string> Text =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".txt", ".text", ".csv", ".log", ".asc", ".tsv",
                ".ini", ".cfg", ".config", ".properties", ".env",
                ".json", ".xml", ".xaml", ".yaml", ".yml", ".md",
                ".markdown", ".rst", ".tex", ".css", ".js", ".ts",
                ".jsx", ".tsx", ".c", ".cpp", ".h", ".hpp", ".cs",
                ".vb", ".java", ".kt", ".swift", ".go", ".py", ".rb",
                ".php", ".pl", ".lua", ".r", ".m", ".scala",
                ".groovy", ".dart", ".pas", ".f90", ".asm", ".s",
                ".bat", ".cmd",".psl", ".sh", ".gitignore",
                ".gitattributes", ".editorconfig", ".dockerignore",
                ".npmrc", ".gitmodules",".csproj", ".vbproj", ".fsproj",
                ".props", ".targets", ".sln", ".manifest",
                ".htm", ".html"
            };
        // ...
    }
}
