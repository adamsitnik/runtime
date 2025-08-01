// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Json
{
    public static partial class HttpClientJsonExtensions
    {
        private static HttpMethod HttpPatch => field ??= new HttpMethod("PATCH");

        private static Task<HttpResponseMessage> PatchAsync(this HttpClient client, string? requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return client.PatchAsync(CreateUri(requestUri), content, cancellationToken);
        }

        private static Task<HttpResponseMessage> PatchAsync(this HttpClient client, Uri? requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            // HttpClient.PatchAsync is not available in .NET Standard 2.0 and .NET Framework
            HttpRequestMessage request = new HttpRequestMessage(HttpPatch, requestUri) { Content = content };
            return client.SendAsync(request, cancellationToken);
        }
    }
}
