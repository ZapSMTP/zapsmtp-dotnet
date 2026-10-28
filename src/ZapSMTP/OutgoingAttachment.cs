using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZapSMTP.Internal;

namespace ZapSMTP
{
    public sealed partial class OutgoingAttachment
    {
        /// <summary>
        /// An attachment from bytes, sent base64-encoded. The content type follows the file name's extension
        /// unless given.
        /// </summary>
        /// <param name="filename">The name the recipient sees.</param>
        /// <param name="content">The bytes.</param>
        /// <param name="contentType">The content type (default: from the extension, when known).</param>
        /// <param name="contentId">A Content-ID, to show the attachment inline (cid:...).</param>
        /// <returns>The attachment.</returns>
        public static OutgoingAttachment FromBytes(string filename, byte[] content, string? contentType = null, string? contentId = null)
        {
            if (filename is null) throw new ArgumentNullException(nameof(filename));
            if (content is null) throw new ArgumentNullException(nameof(content));
            return new OutgoingAttachment
            {
                Filename = filename,
                Content = Convert.ToBase64String(content),
                ContentType = contentType ?? MimeTypes.For(filename),
                ContentId = contentId,
            };
        }

        /// <summary>An attachment from text, sent as UTF-8.</summary>
        /// <param name="filename">The name the recipient sees.</param>
        /// <param name="text">The text.</param>
        /// <param name="contentType">The content type (default: from the extension, when known).</param>
        /// <param name="contentId">A Content-ID, to show the attachment inline (cid:...).</param>
        /// <returns>The attachment.</returns>
        public static OutgoingAttachment FromText(string filename, string text, string? contentType = null, string? contentId = null) =>
            FromBytes(filename, Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))), contentType, contentId);

        /// <summary>An attachment read from a file.</summary>
        /// <param name="path">The file.</param>
        /// <param name="filename">The name the recipient sees (default: the file's name).</param>
        /// <param name="contentType">The content type (default: from the extension, when known).</param>
        /// <param name="contentId">A Content-ID, to show the attachment inline (cid:...).</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The attachment.</returns>
        public static async Task<OutgoingAttachment> FromFileAsync(string path, string? filename = null, string? contentType = null,
            string? contentId = null, CancellationToken cancellationToken = default)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            return FromBytes(filename ?? System.IO.Path.GetFileName(path), buffer.ToArray(), contentType, contentId);
        }
    }
}
