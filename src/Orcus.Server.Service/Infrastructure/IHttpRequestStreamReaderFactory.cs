﻿using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Orcus.Server.Service.Infrastructure
{
    /// <summary>
    ///     Creates <see cref="TextReader" /> instances for reading from <see cref="HttpRequest.Body" />.
    /// </summary>
    public interface IHttpRequestStreamReaderFactory
    {
        /// <summary>
        ///     Creates a new <see cref="TextReader" />.
        /// </summary>
        /// <param name="stream">The <see cref="Stream" />, usually <see cref="HttpRequest.Body" />.</param>
        /// <param name="encoding">The <see cref="Encoding" />, usually <see cref="Encoding.UTF8" />.</param>
        /// <returns>A <see cref="TextReader" />.</returns>
        TextReader CreateReader(Stream stream, Encoding encoding);
    }
}