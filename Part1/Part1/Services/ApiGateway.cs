using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Part1.Models;

namespace Part1.Services
{
    public class CommandRequest
    {
        public string MacAddress { get; set; }
        public string Action { get; set; }        // "ON" / "OFF"
    }

    /// <summary>
    /// Minimal REST API hosted in-process with HttpListener (no extra packages).
    ///   POST /api/telemetry      device -> API   (202 queued, 400 invalid, 503 overloaded)
    ///   GET  /api/devices        live device snapshot
    ///   GET  /api/stats          queue depths / counters
    ///   POST /api/command        {"MacAddress":"..","Action":"ON|OFF"}
    ///   POST /api/command/undo   revert last command (stack pop)
    ///   GET  /api/health
    /// Each request is handled on its own thread-pool task, so slow clients never block others.
    /// Ref: Microsoft (n.d.) HttpListener.
    /// </summary>
    public sealed class ApiGateway
    {
        private readonly TelemetryEngine _engine;
        private readonly HttpListener _listener = new HttpListener();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private volatile bool _running;

        public string BaseUrl { get; private set; }

        public ApiGateway(TelemetryEngine engine, int port)
        {
            _engine = engine;
            BaseUrl = "http://localhost:" + port + "/";
            _listener.Prefixes.Add(BaseUrl);
        }

        public bool Start(out string error)
        {
            try { _listener.Start(); }
            catch (Exception ex) { error = ex.Message; return false; }

            _running = true;
            Task.Run(() => AcceptLoop());
            error = null;
            return true;
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Close(); } catch (Exception) { }
        }

        private async Task AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { break; }                    // listener was closed
                var ignored = Task.Run(() => HandleAsync(ctx)); // don't await: serve concurrently
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                string path = req.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                string method = req.HttpMethod.ToUpperInvariant();

                if (method == "POST" && path == "/api/telemetry")
                {
                    string body = await ReadBodyAsync(req).ConfigureAwait(false);
                    if (body == null) { Write(res, 413, new { error = "Payload too large" }); return; }

                    TelemetryPacket packet;
                    try { packet = _json.Deserialize<TelemetryPacket>(body); }
                    catch (Exception) { Write(res, 400, new { error = "Malformed JSON" }); return; }

                    switch (_engine.Ingest(packet))
                    {
                        case IngestResult.Accepted:
                            Write(res, 202, new { status = "queued" }); break;
                        case IngestResult.Overloaded:
                            res.AddHeader("Retry-After", "1");
                            Write(res, 503, new { error = "Ingestion queue full, retry shortly" }); break;
                        default:
                            Write(res, 400, new { error = "Invalid telemetry packet" }); break;
                    }
                }
                else if (method == "GET" && path == "/api/devices")
                {
                    var rows = _engine.GetDevices().Select(d => new
                    {
                        mac = d.MacAddress,
                        name = d.Name,
                        category = d.Category,
                        metric = d.Metric,
                        value = d.LastValue,
                        unit = d.Unit,
                        status = d.StatusText,
                        actuatorOn = d.IsActuatorOn,
                        lastSeen = d.LastSeen.ToString("o")
                    }).ToList();
                    Write(res, 200, rows);
                }
                else if (method == "GET" && path == "/api/stats")
                {
                    Write(res, 200, _engine.GetStats());
                }
                else if (method == "GET" && path == "/api/health")
                {
                    Write(res, 200, new { status = "ok" });
                }
                else if (method == "POST" && path == "/api/command")
                {
                    string body = await ReadBodyAsync(req).ConfigureAwait(false);
                    CommandRequest cmd = body == null ? null : _json.Deserialize<CommandRequest>(body);
                    if (cmd == null || (cmd.Action != "ON" && cmd.Action != "OFF"))
                    { Write(res, 400, new { error = "Action must be ON or OFF" }); return; }

                    CommandRecord rec = _engine.IssueCommand(cmd.MacAddress, cmd.Action == "ON", "API");
                    if (rec == null) Write(res, 404, new { error = "Unknown device or no actuator" });
                    else Write(res, 200, new { status = "applied", description = rec.Description });
                }
                else if (method == "POST" && path == "/api/command/undo")
                {
                    CommandRecord rec = _engine.UndoLastCommand();
                    if (rec == null) Write(res, 409, new { error = "Nothing to undo" });
                    else Write(res, 200, new { status = "reverted", description = rec.Description });
                }
                else
                {
                    Write(res, 404, new { error = "Not found" });
                }
            }
            catch (Exception ex)
            {
                try { Write(res, 500, new { error = ex.Message }); } catch (Exception) { }
            }
            finally
            {
                res.Close();
            }
        }

        private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
        {
            if (req.ContentLength64 > 8192) return null;          // protect the backend
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private void Write(HttpListenerResponse res, int status, object payload)
        {
            byte[] buf = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            res.StatusCode = status;
            res.ContentType = "application/json";
            res.ContentLength64 = buf.Length;
            res.OutputStream.Write(buf, 0, buf.Length);
        }
    }
}