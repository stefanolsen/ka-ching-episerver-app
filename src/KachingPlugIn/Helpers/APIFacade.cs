﻿using EPiServer.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace KachingPlugIn.Helpers
{
    public static class APIFacade
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(APIFacade));

        public static HttpStatusCode DeleteObject(object model, string url)
        {
            WebRequest request = WebRequest.Create(url);

            request.Method = "DELETE";
            request.ContentType = "application/json";

            using (var dataStream = request.GetRequestStream())
            using (var streamWriter = new StreamWriter(dataStream))
            {
                var serializer = new JsonSerializer
                {
                    ContractResolver = new DefaultContractResolver
                    {
                        NamingStrategy = new SnakeCaseNamingStrategy()
                    },
                    NullValueHandling = NullValueHandling.Ignore
                };

                serializer.Serialize(streamWriter, model);
            }

            HttpWebResponse response = request.GetResponse() as HttpWebResponse;

            return response.StatusCode;
        }

        public static HttpStatusCode Delete(IEnumerable<string> ids, string url)
        {
            string deleteUrl = url + "&ids=" + string.Join(",", ids);
            Logger.Information("Delete url: " + deleteUrl);

            WebRequest request = WebRequest.Create(deleteUrl);
            request.Method = "DELETE";

            HttpWebResponse response = request.GetResponse() as HttpWebResponse;

            return response.StatusCode;
        }

        public static HttpStatusCode Post(object model, string url)
        {
            WebRequest request = WebRequest.Create(url);

            request.Method = "POST";
            request.ContentType = "application/json";

            using (var dataStream = request.GetRequestStream())
            using (var streamWriter = new StreamWriter(dataStream))
            {
                var serializer = new JsonSerializer
                {
                    ContractResolver = new DefaultContractResolver
                    {
                        NamingStrategy = new SnakeCaseNamingStrategy()
                    },
                    NullValueHandling = NullValueHandling.Ignore
                };

                serializer.Serialize(streamWriter, model);
            }

            HttpWebResponse response = request.GetResponse() as HttpWebResponse;

            return response.StatusCode;
        }
    }
}