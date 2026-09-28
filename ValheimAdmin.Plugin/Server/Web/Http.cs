using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace ValheimAdmin.Web
{
    /// <summary>An error with the HTTP status the panel should see (same codes as the agent).</summary>
    public sealed class HttpError : Exception
    {
        public readonly int Status;
        public HttpError(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>A response other than JSON: a file or an empty status.</summary>
    public sealed class HttpResult
    {
        public int Status = 200;
        public byte[] Body;
        public string ContentType;
        public string ETag;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();

        public static HttpResult File(byte[] body, string contentType, string etag = null) =>
            new HttpResult { Body = body, ContentType = contentType, ETag = etag };

        public static HttpResult Empty(int status) => new HttpResult { Status = status, Body = new byte[0], ContentType = "text/plain" };
    }

    public sealed class Request
    {
        public readonly HttpListenerContext Context;
        public readonly string Method;
        public readonly string Path;
        public readonly Dictionary<string, string> Query;
        public readonly Dictionary<string, string> Route = new Dictionary<string, string>();
        public readonly string Ip;
        private string body;

        public Request(HttpListenerContext context)
        {
            Context = context;
            Method = context.Request.HttpMethod.ToUpperInvariant();
            Path = context.Request.Url.AbsolutePath;
            Query = ParseQuery(context.Request.Url.Query);
            Ip = context.Request.RemoteEndPoint?.Address?.ToString() ?? "?";
        }

        public string Header(string name) => Context.Request.Headers[name];

        public string Cookie(string name)
        {
            string header = Header("Cookie");
            if (string.IsNullOrEmpty(header)) return null;
            foreach (string part in header.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) return Uri.UnescapeDataString(part.Substring(eq + 1).Trim());
            }
            return null;
        }

        public string Q(string name) => Query.TryGetValue(name, out string v) ? v : null;

        public long? QLong(string name) => long.TryParse(Q(name), out long v) ? v : (long?)null;

        public int? QInt(string name) => int.TryParse(Q(name), out int v) ? v : (int?)null;

        public string R(string name) => Route[name];

        public string Body()
        {
            if (body != null) return body;
            using (var reader = new StreamReader(Context.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd();
            return body;
        }

        /// <summary>JSON body with case-insensitive keys (the panel sends camelCase).</summary>
        public Dictionary<string, object> JsonBody()
        {
            string text = Body();
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                return new Dictionary<string, object>(Json.ParseObject(text), StringComparer.OrdinalIgnoreCase);
            }
            catch (FormatException e)
            {
                throw new HttpError(400, "Bad JSON: " + e.Message);
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;
            foreach (string pair in query.TrimStart('?').Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }
    }

    /// <summary>Method + path pattern ("/api/snapshots/{id}") to handler.</summary>
    public sealed class Router
    {
        private sealed class Route
        {
            public string Method;
            public string[] Segments;
            public bool Admin;
            public Func<Request, object> Handler;
        }

        private readonly List<Route> routes = new List<Route>();

        public void Map(string method, string pattern, Func<Request, object> handler, bool admin = true)
        {
            routes.Add(new Route { Method = method, Segments = pattern.Trim('/').Split('/'), Admin = admin, Handler = handler });
        }

        /// <summary>The handler for the request, filling route values; admin tells whether it needs a session.</summary>
        public Func<Request, object> Find(Request request, out bool admin, out bool pathKnown)
        {
            admin = true;
            pathKnown = false;
            string[] parts = request.Path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
            foreach (Route route in routes)
            {
                if (route.Segments.Length != parts.Length) continue;
                var values = new Dictionary<string, string>();
                bool match = true;
                for (int i = 0; i < parts.Length && match; i++)
                {
                    string seg = route.Segments[i];
                    if (seg.StartsWith("{", StringComparison.Ordinal) && seg.EndsWith("}", StringComparison.Ordinal))
                        values[seg.Substring(1, seg.Length - 2)] = parts[i];
                    else
                        match = string.Equals(seg, parts[i], StringComparison.OrdinalIgnoreCase);
                }
                if (!match) continue;
                pathKnown = true;
                if (route.Method != request.Method) continue;
                foreach (var kv in values) request.Route[kv.Key] = kv.Value;
                admin = route.Admin;
                return route.Handler;
            }
            return null;
        }
    }

    public static class Respond
    {
        public static void Json(HttpListenerResponse response, int status, object value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(ValheimAdmin.Json.Serialize(value));
            Write(response, status, "application/json; charset=utf-8", bytes);
        }

        public static void Result(HttpListenerRequest request, HttpListenerResponse response, HttpResult result)
        {
            foreach (var kv in result.Headers) response.AddHeader(kv.Key, kv.Value);
            if (result.ETag != null)
            {
                response.AddHeader("ETag", result.ETag);
                response.AddHeader("Cache-Control", "no-cache");
                if (request.Headers["If-None-Match"] == result.ETag)
                {
                    Write(response, 304, null, new byte[0]);
                    return;
                }
            }
            Write(response, result.Status, result.ContentType, result.Body ?? new byte[0]);
        }

        public static void Write(HttpListenerResponse response, int status, string contentType, byte[] body)
        {
            response.StatusCode = status;
            if (contentType != null) response.ContentType = contentType;
            response.AddHeader("X-Content-Type-Options", "nosniff");
            response.ContentLength64 = body.Length;
            if (body.Length > 0) response.OutputStream.Write(body, 0, body.Length);
            response.OutputStream.Close();
        }
    }
}
