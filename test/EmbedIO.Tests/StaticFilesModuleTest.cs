﻿using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Utilities;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class StaticFilesModuleTest : EndToEndFixtureBase
    {
        protected StaticFilesModuleTest()
            : base(false)
        {
            ServedFolder = new StaticFolder.WithDataFiles(nameof(StaticFilesModuleTest));
        }

        protected StaticFolder.WithDataFiles ServedFolder { get; }

        protected override void Dispose(bool disposing)
        {
            ServedFolder.Dispose();
        }

        protected override void OnSetUp()
        {
            Server
                .WithStaticFolder("/", StaticFolder.RootPathOf(nameof(StaticFilesModuleTest)), true);
        }

        public class GetFiles : StaticFilesModuleTest
        {
            [Test]
            public async Task Index()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, UrlPath.Root);

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");

                    var html = await response.Content.ReadAsStringAsync();

                    Assert.AreEqual(Resources.Index, html, "Same content index.html");

                    Assert.IsTrue(string.IsNullOrWhiteSpace(response.Headers.Pragma.ToString()), "Pragma empty");
                }

                request = new HttpRequestMessage(HttpMethod.Get, UrlPath.Root);

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");
                }
            }

            [TestCase("sub/")]
            [TestCase("sub")]
            public async Task SubFolderIndex(string url)
            {
                var html = await Client.GetStringAsync(url);
                Assert.AreEqual(Resources.SubIndex, html, $"Same content {url}");
            }

            [Test]
            public async Task TestHeadIndex()
            {
                var request = new HttpRequestMessage(HttpMethod.Head, UrlPath.Root);

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");

                    var html = await response.Content.ReadAsStringAsync();

                    Assert.IsEmpty(html, "Content Empty");
                }
            }

            [Test]
            public async Task FileWritable()
            {
                var root = Path.GetTempPath();
                var file = Path.Combine(root, "index.html");
                File.WriteAllText(file, Resources.Index);

                using (var server = new TestWebServer())
                {
                    server
                        .WithStaticFolder("/", root, false)
                        .Start();

                    var remoteFile = await server.Client.GetStringAsync(UrlPath.Root);
                    File.WriteAllText(file, Resources.SubIndex);

                    var remoteUpdatedFile = await server.Client.GetStringAsync(UrlPath.Root);
                    File.WriteAllText(file, nameof(WebServer));

                    Assert.AreEqual(Resources.Index, remoteFile);
                    Assert.AreEqual(Resources.SubIndex, remoteUpdatedFile);
                }
            }

            [Test]
            public async Task SensitiveFile()
            {
                var file = Path.GetTempPath() + Guid.NewGuid().ToString().ToLower();
                File.WriteAllText(file, string.Empty);

                Assert.IsTrue(File.Exists(file), "File was created");

                if (File.Exists(file.ToUpper()))
                {
                    Assert.Ignore("File-system is not case sensitive.");
                }

                var htmlUpperCase = await Client.GetStringAsync(StaticFolder.WithDataFiles.UppercaseFile);
                Assert.AreEqual(nameof(StaticFolder.WithDataFiles.UppercaseFile), htmlUpperCase, "Same content upper case");

                var htmlLowerCase = await Client.GetStringAsync(StaticFolder.WithDataFiles.LowercaseFile);
                Assert.AreEqual(nameof(StaticFolder.WithDataFiles.LowercaseFile), htmlLowerCase, "Same content lower case");
            }
        }

        public class GetPartials : StaticFilesModuleTest
        {
            [TestCase("Got initial part of file", 0, 1024)]
            [TestCase("Got middle part of file", StaticFolder.WithDataFiles.BigDataSize / 2, 1024)]
            [TestCase("Got final part of file", StaticFolder.WithDataFiles.BigDataSize - 1024, 1024)]
            public async Task GetPartialContent(string message, int offset, int length)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, StaticFolder.WithDataFiles.BigDataFile);
                request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode, "Responds with 216 Partial Content");

                    using (var ms = new MemoryStream())
                    {
                        var responseStream = await response.Content.ReadAsStreamAsync();
                        responseStream.CopyTo(ms);
                        var data = ms.ToArray();
                        Assert.IsTrue(ServedFolder.BigData.Skip(offset).Take(length).SequenceEqual(data), message);
                    }
                }
            }

            [Test]
            public async Task NotPartial()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, StaticFolder.WithDataFiles.BigDataFile);

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");

                    var data = await response.Content.ReadAsByteArrayAsync();

                    Assert.IsNotNull(data, "Data is not empty");
                    Assert.IsTrue(ServedFolder.BigData.SequenceEqual(data));
                }
            }

            [Test]
            public async Task ReconstructFileFromPartials()
            {
                var requestHead = new HttpRequestMessage(HttpMethod.Get, StaticFolder.WithDataFiles.BigDataFile);

                int remoteSize;
                using (var res = await Client.SendAsync(requestHead))
                {
                    remoteSize = (await res.Content.ReadAsByteArrayAsync()).Length;
                }

                Assert.AreEqual(StaticFolder.WithDataFiles.BigDataSize, remoteSize);

                var buffer = new byte[remoteSize];
                const int chunkSize = 100000;
                for (var offset = 0; offset < remoteSize; offset += chunkSize)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, StaticFolder.WithDataFiles.BigDataFile);
                    var top = Math.Min(offset + chunkSize, remoteSize) - 1;

                    request.Headers.Range = new RangeHeaderValue(offset, top);

                    using (var response = await Client.SendAsync(request))
                    {
                        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);

                        using (var ms = new MemoryStream())
                        {
                            var stream = await response.Content.ReadAsStreamAsync();
                            stream.CopyTo(ms);
                            Buffer.BlockCopy(ms.GetBuffer(), 0, buffer, offset, (int)ms.Length);
                        }
                    }
                }

                Assert.IsTrue(ServedFolder.BigData.SequenceEqual(buffer));
            }

            [Test]
            public async Task InvalidRange_RespondsWith416()
            {
                var requestHead = new HttpRequestMessage(HttpMethod.Get, WebServerUrl + StaticFolder.WithDataFiles.BigDataFile);

                using (var res = await Client.SendAsync(requestHead))
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, WebServerUrl + StaticFolder.WithDataFiles.BigDataFile);
                    request.Headers.Range = new RangeHeaderValue(0, StaticFolder.WithDataFiles.BigDataSize + 10);

                    using (var response = await Client.SendAsync(request))
                    {
                        Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
                        Assert.AreEqual(StaticFolder.WithDataFiles.BigDataSize, response.Content.Headers.ContentRange.Length);
                    }
                }
            }
        }

        public class CompressFile : StaticFilesModuleTest
        {
            [Test]
            public async Task GetGzip()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, UrlPath.Root);
                request.Headers.AcceptEncoding.Clear();
                byte[] compressedBytes;
                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");
                    using (var memoryStream = new MemoryStream())
                    {
                        using (var compressor = new GZipStream(memoryStream, CompressionMode.Compress))
                        using (var responseStream = await response.Content.ReadAsStreamAsync())
                            responseStream.CopyTo(compressor);

                        compressedBytes = memoryStream.ToArray();
                    }
                }

                request = new HttpRequestMessage(HttpMethod.Get, UrlPath.Root);
                request.Headers.AcceptEncoding.Clear();
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(CompressionMethodNames.Gzip));
                byte[] compressedResponseBytes;
                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");
                    compressedResponseBytes = await response.Content.ReadAsByteArrayAsync();
                }

                Assert.IsTrue(compressedResponseBytes.SequenceEqual(compressedBytes));
            }
        }

        public class Etag : StaticFilesModuleTest
        {
            [Test]
            public async Task GetEtag()
            {
                var request = new HttpRequestMessage(HttpMethod.Get, WebServerUrl);
                string entityTag;

                using (var response = await Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Status Code OK");

                    // Can't use response.Headers.Etag, it's always null
                    Assert.NotNull(response.Headers.FirstOrDefault(x => x.Key == "ETag"), "ETag is not null");
                    entityTag = response.Headers.First(x => x.Key == "ETag").Value.First();
                }

                var secondRequest = new HttpRequestMessage(HttpMethod.Get, WebServerUrl);
                secondRequest.Headers.TryAddWithoutValidation(HttpHeaderNames.IfNoneMatch, entityTag);

                using (var response = await Client.SendAsync(secondRequest))
                {
                    Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode, "Status Code NotModified");
                }
            }
        }
    }
}