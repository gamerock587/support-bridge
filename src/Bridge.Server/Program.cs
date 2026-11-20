using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using JedarBridge;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("BRIDGE_LISTEN") ?? "http://127.0.0.1:4353");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 9_000_000);
var data = Path.GetFullPath(Environment.GetEnvironmentVariable("BRIDGE_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"));
Directory.CreateDirectory(data);
if (OperatingSystem.IsWindows())
{
    var acl = new DirectorySecurity();
    acl.SetAccessRuleProtection(true, false);
    foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(data).SetAccessControl(acl);
}
var tokenFile = Path.Combine(data, "operator-token.txt");
if (!File.Exists(tokenFile)) File.WriteAllText(tokenFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = File.ReadAllText(tokenFile).Trim();
if (token.Length < 32) throw new InvalidOperationException("Operator token must contain at least 32 characters.");
var sessions = new ConcurrentDictionary<string, Device>();
var jobs = new ConcurrentDictionary<string, Job>();
var gate = new object();
var auditGate = new object();
void Audit(string action, string id, string? job = null)
{
    lock (auditGate) File.AppendAllText(Path.Combine(data, "audit.jsonl"),
        JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, action, device = id, job }) + "\n");
}
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = ctx.Request.Headers.Authorization.ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token))))
        { ctx.Response.StatusCode = 401; return; }
    }
    await next();
});
app.MapGet("/health", () => Results.Ok(new { service = "JedarBridge", status = "ok", version = 1 }));
app.MapGet("/", () => Results.Text("Jedar Bridge is online. Remote support requires local consent, a pairing code, and an operator token.\n"));
app.MapGet("/download/client", () =>
{
    var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Client", "Bridge.Client.exe"));
    return File.Exists(exe) ? Results.File(exe, "application/octet-stream", "Bridge.Client.exe") : Results.NotFound();
});
app.MapGet("/api/devices", () => sessions.Values.Where(d => d.Claimed).Select(d => new { d.Id, d.Name, d.User, d.ConnectedAt, busy = d.ActiveJob != null }));
app.MapPost("/api/pair", (Pair input) =>
{
    lock (gate)
    {
        var d = sessions.Values.FirstOrDefault(d => !d.Claimed && d.Code == input.Code && d.ConnectedAt.AddMinutes(10) > DateTimeOffset.UtcNow);
        if (d == null) return Results.NotFound(new { error = "Invalid or expired pairing code." });
        d.Claimed = true; Audit("paired", d.Id);
        return Results.Ok(new { deviceId = d.Id, d.Name, d.User });
    }
});
app.MapPost("/api/devices/{id}/jobs", async (string id, Work work) =>
{
    if (work.Op is not ("exec" or "list" or "read" or "write" or "delete")) return Results.BadRequest(new { error = "Unknown operation." });
    if (work.TimeoutSeconds is < 1 or > 600) return Results.BadRequest(new { error = "Timeout must be 1-600 seconds." });
    if (!sessions.TryGetValue(id, out var d) || !d.Claimed) return Results.NotFound();
    Job job;
    lock (gate)
    {
        if (d.ActiveJob != null) return Results.Conflict(new { error = "Device already has an active job." });
        if (jobs.Count >= 100 && jobs.Values.All(j => j.CompletedAt == null)) return Results.StatusCode(503);
        foreach (var old in jobs.Values.Where(j => j.CompletedAt != null).OrderBy(j => j.CompletedAt).Take(Math.Max(0, jobs.Count - 99))) jobs.TryRemove(old.Id, out _);
        job = new Job { DeviceId = id }; jobs[job.Id] = job; d.ActiveJob = job.Id;
    }
    Audit(work.Op, id, job.Id);
    try { await Wire.Send(d.Socket, new { type = "job", jobId = job.Id, work }, d.Stop.Token); }
    catch { Finish(job, "disconnected", null); d.Stop.Cancel(); }
    _ = WatchJob(job, d, work.TimeoutSeconds);
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
});
async Task WatchJob(Job job, Device device, int timeout)
{
    try { await Task.Delay(TimeSpan.FromSeconds(timeout + 15), device.Stop.Token); }
    catch (OperationCanceledException) { return; }
    lock (gate)
    {
        if (job.Status != "running") return;
        Finish(job, "timeout", null);
        device.Stop.Cancel(); device.Socket.Abort();
    }
}
app.MapGet("/api/jobs/{id}", (string id) => jobs.TryGetValue(id, out var j) ? Results.Ok(j) : Results.NotFound());
app.MapDelete("/api/devices/{id}", (string id) =>
{
    if (!sessions.TryGetValue(id, out var d) || !d.Claimed) return Results.NotFound();
    Audit("revoked", id); d.Stop.Cancel(); d.Socket.Abort(); return Results.NoContent();
});
void Finish(Job j, string status, JsonElement? result)
{
    lock (gate)
    {
        j.Result = result; j.Status = status; j.CompletedAt = DateTimeOffset.UtcNow;
        if (sessions.TryGetValue(j.DeviceId, out var d) && d.ActiveJob == j.Id) d.ActiveJob = null;
    }
}
app.Map("/agent", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    Device d;
    lock (gate)
    {
        if (sessions.Count >= 100) { ctx.Response.StatusCode = 503; return; }
        string code;
        do { code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(); } while (sessions.Values.Any(x => x.Code == code));
        d = new Device { Code = code }; sessions[d.Id] = d;
    }
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    stop.CancelAfter(TimeSpan.FromHours(8)); d.Stop = stop;
    try
    {
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync(); d.Socket = socket;
        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        helloTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var hello = await Wire.Receive(socket, helloTimeout.Token);
        if (hello == null || hello.Value.GetProperty("type").GetString() != "hello") return;
        d.Name = (hello.Value.GetProperty("name").GetString() ?? "Unknown");
        d.Name = d.Name[..Math.Min(128, d.Name.Length)];
        d.User = hello.Value.GetProperty("user").GetString() ?? "Unknown";
        await Wire.Send(socket, new { type = "welcome", code = d.Code, deviceId = d.Id }, stop.Token);
        using var pairingTimeout = new Timer(_ => { if (!d.Claimed) stop.Cancel(); }, null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
        while (!stop.IsCancellationRequested)
        {
            var msg = await Wire.Receive(socket, stop.Token);
            if (msg == null) break;
            if (msg.Value.GetProperty("type").GetString() == "result")
            {
                var jid = msg.Value.GetProperty("jobId").GetString()!;
                if (d.ActiveJob == jid && jobs.TryGetValue(jid, out var job))
                { Finish(job, "completed", msg.Value.GetProperty("result").Clone()); Audit("completed", d.Id, jid); }
            }
        }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
    { app.Logger.LogInformation("Device {Id} disconnected ({Reason})", d.Id, ex.GetType().Name); }
    finally
    {
        if (d.ActiveJob != null && jobs.TryGetValue(d.ActiveJob, out var job)) Finish(job, "disconnected", null);
        sessions.TryRemove(d.Id, out _); Audit("disconnected", d.Id);
    }
});
app.Logger.LogInformation("Operator token file: {Path}. Keep this file private.", tokenFile);
await app.RunAsync();

record Pair(string Code);
sealed class Device
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Code { get; init; }
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public bool Claimed { get; set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public string? ActiveJob { get; set; }
    public WebSocket Socket { get; set; } = null!;
    public CancellationTokenSource Stop { get; set; } = null!;
}
sealed class Job
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string DeviceId { get; init; }
    public string Status { get; set; } = "running";
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public JsonElement? Result { get; set; }
}
