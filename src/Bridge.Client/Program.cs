using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using JedarBridge;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "Jedar Bridge - Remote Support";
var server = args.FirstOrDefault() ?? "https://support.example.com";
if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
    (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
{
    Console.WriteLine("Use an https:// server URL (http:// is allowed only on localhost)."); return;
}
var admin = OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"JEDAR REMOTE SUPPORT\nServer: {uri}\nAdministrator: {admin}");
if (!admin && Environment.GetEnvironmentVariable("BRIDGE_TEST_NONADMIN") != "1")
{ Console.WriteLine("Run this program as administrator. Press Enter to close."); Console.ReadLine(); return; }
Console.WriteLine("Your company operator can run commands and read, change, or delete files on this computer.");
Console.WriteLine("Only continue for a support session you requested. Close this window to end access.");
Console.Write("Type ALLOW to start: ");
if (Console.ReadLine() != "ALLOW") return;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    using var socket = new ClientWebSocket();
    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    var wsUri = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/agent", Query = "" }.Uri;
    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
    await socket.ConnectAsync(wsUri, connectTimeout.Token);
    await Wire.Send(socket, new { type = "hello", name = Environment.MachineName, user = Environment.UserName }, stop.Token);
    var welcome = await Wire.Receive(socket, stop.Token) ?? throw new IOException("Server closed connection.");
    Console.WriteLine($"\nPAIRING CODE: {welcome.GetProperty("code").GetString()}\nGive this code to your company support operator. Expires in 10 minutes.");
    // Receive continuously, so a disconnected server cancels an active command immediately.
    Task? active = null;
    try
    {
        while (!stop.IsCancellationRequested)
        {
            var msg = await Wire.Receive(socket, stop.Token);
            if (msg == null) break;
            if (msg.Value.GetProperty("type").GetString() != "job") continue;
            if (active != null) await active;
            var jid = msg.Value.GetProperty("jobId").GetString()!;
            var work = msg.Value.GetProperty("work").Deserialize<Work>(Wire.Json)!;
            Console.WriteLine($"{DateTime.Now:T} Support operation: {work.Op} {work.Path}");
            active = Task.Run(async () =>
            {
                object result;
                try { result = await Execute(work, stop.Token); }
                catch (Exception ex) { result = new { ok = false, error = ex.Message }; }
                await Wire.Send(socket, new { type = "result", jobId = jid, result }, stop.Token);
            });
        }
    }
    finally
    {
        stop.Cancel();
        if (active != null) { try { await active; } catch { } }
    }
}
catch (Exception ex) { Console.WriteLine($"Session ended: {ex.Message}"); }
Console.WriteLine("Remote access ended. Press Enter to close."); Console.ReadLine();

static async Task<object> Execute(Work w, CancellationToken stop)
{
    stop.ThrowIfCancellationRequested();
    switch (w.Op)
    {
        case "exec":
            if (string.IsNullOrWhiteSpace(w.Command)) throw new ArgumentException("command is required");
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true, WorkingDirectory = w.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            info.ArgumentList.Add("-NoLogo"); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $OutputEncoding = [Text.Encoding]::UTF8; " + w.Command)));
            using (var process = Process.Start(info) ?? throw new IOException("Could not start PowerShell."))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(w.TimeoutSeconds, 1, 600)));
                var stdout = Drain(process.StandardOutput, deadline.Token); var stderr = Drain(process.StandardError, deadline.Token);
                bool timedOut = false;
                try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).WaitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    await process.WaitForExitAsync();
                }
                return new { ok = !timedOut && process.ExitCode == 0, exitCode = process.ExitCode,
                    timedOut, stdout = await stdout, stderr = await stderr };
            }
        case "list":
            var entries = Directory.EnumerateFileSystemEntries(RequirePath(w)).Take(2001).ToArray();
            return new { ok = true, truncated = entries.Length > 2000, entries = entries.Take(2000).Select(p =>
                new { path = p, directory = Directory.Exists(p) }).ToArray() };
        case "read":
            var path = RequirePath(w);
            await using (var file = File.OpenRead(path))
            {
                if (file.Length > 4_000_000) throw new IOException("Maximum file size is 4 MB. Use exec for larger files.");
                using var memory = new MemoryStream();
                await file.CopyToAsync(memory, stop);
                if (memory.Length > 4_000_000) throw new IOException("File grew beyond the limit.");
                return new { ok = true, contentBase64 = Convert.ToBase64String(memory.ToArray()) };
            }
        case "write":
            var bytes = Convert.FromBase64String(w.ContentBase64 ?? throw new ArgumentException("contentBase64 required"));
            if (bytes.Length > 4_000_000) throw new IOException("Maximum file size is 4 MB.");
            await using (var output = new FileStream(RequirePath(w), w.Overwrite ? FileMode.Create : FileMode.CreateNew))
                await output.WriteAsync(bytes, stop);
            return new { ok = true, bytes = bytes.Length };
        case "delete":
            var target = RequirePath(w);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
            else if (File.Exists(target)) File.Delete(target);
            else throw new FileNotFoundException("Path does not exist.", target);
            return new { ok = true };
        default: throw new ArgumentException("Unknown operation");
    }
}
static string RequirePath(Work w) => Path.GetFullPath(w.Path ?? throw new ArgumentException("path is required"));
static async Task<object> Drain(StreamReader reader, CancellationToken ct)
{
    var buffer = new char[4096]; var text = new StringBuilder(); bool truncated = false; int n;
    try
    {
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(n, 200_000 - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            if (keep < n) truncated = true;
        }
    }
    catch (OperationCanceledException) { truncated = true; }
    return new { text = text.ToString(), truncated };
}
