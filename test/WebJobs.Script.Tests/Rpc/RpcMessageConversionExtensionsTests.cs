﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.Rpc;
using Microsoft.WebJobs.Script.Tests;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Rpc
{
    public class RpcMessageConversionExtensionsTests
    {
        private static readonly string TestImageLocation = "Rpc\\Resources\\functions.png";

        [Theory]
        [InlineData("application/x-www-form-urlencoded’", "say=Hi&to=Mom")]
        public void HttpObjects_StringBody(string expectedContentType, object body)
        {
            var logger = MockNullLoggerFactory.CreateLogger();
            var capabilities = new Capabilities(logger);

            var headers = new HeaderDictionary();
            headers.Add("content-type", expectedContentType);
            HttpRequest request = HttpTestHelpers.CreateHttpRequest("GET", "http://localhost/api/httptrigger-scenarios", headers, body);

            var rpcRequestObject = request.ToRpc(logger, capabilities);
            Assert.Equal(body.ToString(), rpcRequestObject.Http.Body.String);

            string contentType;
            rpcRequestObject.Http.Headers.TryGetValue("content-type", out contentType);
            Assert.Equal(expectedContentType, contentType);
        }

        [Theory]
        [InlineData("say=Hi&to=Mom", new string[] { "say", "to" }, new string[] { "Hi", "Mom" })]
        [InlineData("say=Hi", new string[] { "say" }, new string[] { "Hi" })]
        [InlineData("say=Hi&to=", new string[] { "say" }, new string[] { "Hi" })] // Removes empty value query params
        public void HttpObjects_Query(string queryString, string[] expectedKeys, string[] expectedValues)
        {
            var logger = MockNullLoggerFactory.CreateLogger();
            var capabilities = new Capabilities(logger);

            HttpRequest request = HttpTestHelpers.CreateHttpRequest("GET", $"http://localhost/api/httptrigger-scenarios?{queryString}");

            var rpcRequestObject = request.ToRpc(logger, capabilities);
            // Same number of expected key value pairs
            Assert.Equal(rpcRequestObject.Http.Query.Count, expectedKeys.Length);
            Assert.Equal(rpcRequestObject.Http.Query.Count, expectedValues.Length);
            // Same key and value strings for each pair
            for (int i = 0; i < expectedKeys.Length; i++)
            {
                Assert.True(rpcRequestObject.Http.Query.ContainsKey(expectedKeys[i]));
                Assert.Equal(rpcRequestObject.Http.Query.GetValueOrDefault(expectedKeys[i]), expectedValues[i]);
            }
        }

        [Theory]
        [InlineData(BindingDirection.In, "blob", DataType.String)]
        [InlineData(BindingDirection.Out, "blob", DataType.Binary)]
        [InlineData(BindingDirection.InOut, "blob", DataType.Stream)]
        [InlineData(BindingDirection.InOut, "blob", DataType.Undefined)]
        public void ToBindingInfo_Converts_Correctly(BindingDirection bindingDirection, string type, DataType dataType)
        {
            BindingMetadata bindingMetadata = new BindingMetadata
            {
                Direction = bindingDirection,
                Type = type,
                DataType = dataType
            };

            BindingInfo bindingInfo = bindingMetadata.ToBindingInfo();

            Assert.Equal(bindingInfo.Direction, (BindingInfo.Types.Direction)bindingMetadata.Direction);
            Assert.Equal(bindingInfo.Type, bindingMetadata.Type);
            Assert.Equal(bindingInfo.DataType, (BindingInfo.Types.DataType)bindingMetadata.DataType);
        }

        [Fact]
        public void ToBindingInfo_Defaults_EmptyDataType()
        {
            BindingMetadata bindingMetadata = new BindingMetadata
            {
                Direction = BindingDirection.In,
                Type = "blob"
            };

            BindingInfo bindingInfo = bindingMetadata.ToBindingInfo();

            Assert.Equal(bindingInfo.Direction, (BindingInfo.Types.Direction)bindingMetadata.Direction);
            Assert.Equal(bindingInfo.Type, bindingMetadata.Type);
            Assert.Equal(bindingInfo.DataType, BindingInfo.Types.DataType.Undefined);
        }

        [Theory]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, null, null, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Strict, null, null, null, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.None, null, null, null, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, "4/17/2019", null, null, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, "bing.com", null, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, null, true, null, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, null, null, 60 * 60 * 24, null, null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, null, null, null, "/example/route", null)]
        [InlineData("testuser", "testvalue", RpcHttpCookie.Types.SameSite.Lax, null, null, null, null, null, true)]
        public void SetCookie_ReturnsExpectedResult(string name, string value, RpcHttpCookie.Types.SameSite sameSite, string expires,
            string domain, bool? httpOnly, double? maxAge, string path, bool? secure)
        {
            // Mock rpc cookie
            var rpcCookie = new RpcHttpCookie()
            {
                Name = name,
                Value = value,
                SameSite = sameSite
            };

            if (!string.IsNullOrEmpty(domain))
            {
                rpcCookie.Domain = new NullableString()
                {
                    Value = domain
                };
            }

            if (!string.IsNullOrEmpty(path))
            {
                rpcCookie.Path = new NullableString()
                {
                    Value = path
                };
            }

            if (maxAge.HasValue)
            {
                rpcCookie.MaxAge = new NullableDouble()
                {
                    Value = maxAge.Value
                };
            }

            DateTimeOffset? expiresDateTime = null;
            if (!string.IsNullOrEmpty(expires))
            {
                if (DateTimeOffset.TryParse(expires, out DateTimeOffset result))
                {
                    expiresDateTime = result;
                    rpcCookie.Expires = new NullableTimestamp()
                    {
                        Value = result.ToTimestamp()
                    };
                }
            }

            if (httpOnly.HasValue)
            {
                rpcCookie.HttpOnly = new NullableBool()
                {
                    Value = httpOnly.Value
                };
            }

            if (secure.HasValue)
            {
                rpcCookie.Secure = new NullableBool()
                {
                    Value = secure.Value
                };
            }

            var appendCookieArguments = Utilities.RpcHttpCookieConverter(rpcCookie);
            Assert.Equal(appendCookieArguments.Item1, name);
            Assert.Equal(appendCookieArguments.Item2, value);

            var cookieOptions = appendCookieArguments.Item3;
            Assert.Equal(cookieOptions.Domain, domain);
            Assert.Equal(cookieOptions.Path, path ?? "/");

            Assert.Equal(cookieOptions.MaxAge?.TotalSeconds, maxAge);
            Assert.Equal(cookieOptions.Expires?.UtcDateTime.ToString(), expiresDateTime?.UtcDateTime.ToString());

            Assert.Equal(cookieOptions.Secure, secure ?? false);
            Assert.Equal(cookieOptions.HttpOnly, httpOnly ?? false);
        }

        [Fact]
        public void HttpObjects_ClaimsPrincipal()
        {
            var logger = MockNullLoggerFactory.CreateLogger();
            var capabilities = new Capabilities(logger);

            HttpRequest request = HttpTestHelpers.CreateHttpRequest("GET", $"http://localhost/apihttptrigger-scenarios");

            var claimsIdentity1 = MockEasyAuth("facebook", "Connor McMahon", "10241897674253170");
            var claimsIdentity2 = new ClaimsIdentity(new List<Claim>
            {
                new Claim("http://schemas.microsoft.com/2017/07/functions/claims/authlevel", "Function")
            }, "WebJobsAuthLevel");
            var claimsIdentity3 = new ClaimsIdentity();
            var claimsIdentities = new List<ClaimsIdentity> { claimsIdentity1, claimsIdentity2, claimsIdentity3 };

            request.HttpContext.User = new ClaimsPrincipal(claimsIdentities);

            var rpcRequestObject = request.ToRpc(logger, capabilities);

            var identities = request.HttpContext.User.Identities.ToList();
            var rpcIdentities = rpcRequestObject.Http.Identities.ToList();

            Assert.Equal(claimsIdentities.Count, rpcIdentities.Count);

            for (int i = 0; i < identities.Count; i++)
            {
                var identity = identities[i];
                var rpcIdentity = rpcIdentities.ElementAtOrDefault(i);

                Assert.NotNull(identity);
                Assert.NotNull(rpcIdentity);

                Assert.Equal(rpcIdentity.AuthenticationType?.Value, identity.AuthenticationType);
                Assert.Equal(rpcIdentity.NameClaimType?.Value, identity.NameClaimType);
                Assert.Equal(rpcIdentity.RoleClaimType?.Value, identity.RoleClaimType);

                var claims = identity.Claims.ToList();
                for (int j = 0; j < claims.Count; j++)
                {
                    Assert.True(rpcIdentity.Claims.ElementAtOrDefault(j) != null);
                    Assert.Equal(rpcIdentity.Claims[j].Type, claims[j].Type);
                    Assert.Equal(rpcIdentity.Claims[j].Value, claims[j].Value);
                }
            }
        }

        internal static ClaimsIdentity MockEasyAuth(string provider, string name, string id)
        {
            var claims = new List<Claim>
            {
                new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", name),
                new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn", name),
                new Claim("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", id)
            };

            var identity = new ClaimsIdentity(
                claims,
                provider,
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
                "http://schemas.microsoft.com/ws/2008/06/identity/claims/role");

            return identity;
        }

        [Theory]
        [InlineData("application/octet-stream", "true")]
        [InlineData("image/png", "true")]
        [InlineData("application/octet-stream", null)]
        [InlineData("image/png", null)]
        public void HttpObjects_RawBodyBytes_Image_Length(string contentType, string rawBytesEnabled)
        {
            var logger = MockNullLoggerFactory.CreateLogger();
            var capabilities = new Capabilities(logger);
            if (!string.Equals(rawBytesEnabled, null))
            {
                capabilities.UpdateCapabilities(new MapField<string, string>
                {
                    { LanguageWorkerConstants.RawHttpBodyBytes, rawBytesEnabled.ToString() }
                });
            }

            FileStream image = new FileStream(TestImageLocation, FileMode.Open, FileAccess.Read);
            byte[] imageBytes = FileStreamToBytes(image);
            string imageString = Encoding.UTF8.GetString(imageBytes);

            long imageBytesLength = imageBytes.Length;
            long imageStringLength = imageString.Length;

            var headers = new HeaderDictionary();
            headers.Add("content-type", contentType);
            HttpRequest request = HttpTestHelpers.CreateHttpRequest("GET", "http://localhost/api/httptrigger-scenarios", headers, imageBytes);

            var rpcRequestObject = request.ToRpc(logger, capabilities);
            if (string.IsNullOrEmpty(rawBytesEnabled))
            {
                Assert.Empty(rpcRequestObject.Http.RawBody.Bytes);
                Assert.Equal(imageStringLength, rpcRequestObject.Http.RawBody.String.Length);
            }
            else
            {
                Assert.Empty(rpcRequestObject.Http.RawBody.String);
                Assert.Equal(imageBytesLength, rpcRequestObject.Http.RawBody.Bytes.ToByteArray().Length);
            }
        }

        private byte[] FileStreamToBytes(FileStream file)
        {
            using (var memoryStream = new MemoryStream())
            {
                file.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
    }
}