using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ZapSMTP.Internal
{
    /// <summary>A file upload, buffered so that every attempt sends the same bytes.</summary>
    internal sealed class MultipartForm
    {
        private readonly string _field;
        private readonly byte[] _bytes;
        private readonly string? _name;
        private readonly string _filename;

        private MultipartForm(string field, byte[] bytes, string? name, string filename)
        {
            _field = field;
            _bytes = bytes;
            _name = name;
            _filename = filename;
        }

        public static async Task<MultipartForm> CreateAsync(string field, Stream file, string? name, string? filename, CancellationToken cancellationToken)
        {
            if (file is null) throw new ArgumentNullException(nameof(file));
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
            return new MultipartForm(field, buffer.ToArray(), name, filename ?? name ?? "image");
        }

        public HttpContent ToContent()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(_bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeTypes.For(_filename) ?? "application/octet-stream");
            form.Add(file, _field, _filename);
            if (_name is not null) form.Add(new StringContent(_name), "name");
            return form;
        }
    }
}
