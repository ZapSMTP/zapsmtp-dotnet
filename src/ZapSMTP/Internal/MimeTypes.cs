using System;
using System.Collections.Generic;
using System.IO;

namespace ZapSMTP.Internal
{
    internal static class MimeTypes
    {
        private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif", [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".txt"] = "text/plain",
            [".csv"] = "text/csv", [".html"] = "text/html", [".ics"] = "text/calendar", [".json"] = "application/json",
            [".xml"] = "application/xml", [".zip"] = "application/zip", [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };

        /// <summary>The content type of a file name's extension, when known.</summary>
        public static string? For(string filename) =>
            ByExtension.TryGetValue(Path.GetExtension(filename), out var type) ? type : null;
    }
}
