"""End-to-end tests using the real server and client. No third-party Python packages."""
import base64, json, os, pathlib, queue, re, shutil, subprocess, tempfile, threading, time
import urllib.request, urllib.error

ROOT = pathlib.Path(__file__).resolve().parents[1]
DOTNET = pathlib.Path(os.environ["LOCALAPPDATA"]) / "JedarBridge/dotnet/dotnet.exe"
if not DOTNET.exists(): DOTNET = shutil.which("dotnet") or "dotnet"
URL = "http://127.0.0.1:14353"

def main():
    with tempfile.TemporaryDirectory(prefix="jedar-test-") as tmp:
        env = dict(os.environ, BRIDGE_DATA=str(pathlib.Path(tmp)/"data"), BRIDGE_LISTEN=URL,
                   BRIDGE_TEST_NONADMIN="1")
        log = open(pathlib.Path(tmp)/"server.log", "w")
        server = subprocess.Popen([str(DOTNET), str(ROOT/"src/Bridge.Server/bin/Release/net10.0/Bridge.Server.dll")],
                                  env=env, stdout=log, stderr=log)
        client = None
        try:
            for _ in range(100):
                try:
                    urllib.request.urlopen(URL+"/health", timeout=1)
                    break
                except Exception: time.sleep(.1)
            else: raise AssertionError("Server did not start")
            token = (pathlib.Path(tmp)/"data/operator-token.txt").read_text().strip()
            def call(method, route, body=None, auth=True):
                headers = {"Content-Type":"application/json"}
                if auth: headers["Authorization"] = "Bearer "+token
                req = urllib.request.Request(URL+route, method=method, headers=headers,
                    data=None if body is None else json.dumps(body).encode())
                with urllib.request.urlopen(req, timeout=10) as response:
                    data = response.read()
                    return json.loads(data) if data else None
            def expect(code, method, path, body=None, auth=True):
                try: call(method,path,body,auth)
                except urllib.error.HTTPError as ex: assert ex.code == code, (ex.code,code)
                else: raise AssertionError(f"Expected {code}")
            expect(401,"GET","/api/devices",auth=False)
            expect(404,"POST","/api/pair",{"code":"000000"})
            client = subprocess.Popen([str(DOTNET), str(ROOT/"src/Bridge.Client/bin/Release/net10.0/Bridge.Client.dll"), URL],
                env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, encoding="utf-8")
            lines = queue.Queue()
            threading.Thread(target=lambda: [lines.put(line) for line in client.stdout], daemon=True).start()
            client.stdin.write("ALLOW\n"); client.stdin.flush()
            code = None
            deadline = time.time()+20
            while time.time()<deadline:
                try: line=lines.get(timeout=1)
                except queue.Empty: continue
                match = re.search(r"PAIRING CODE: (\d+)",line)
                if match: code=match[1]; break
            assert code, "Client did not receive pairing code"
            device=call("POST","/api/pair",{"code":code})["deviceId"]
            expect(404,"POST","/api/pair",{"code":code})
            assert len(call("GET","/api/devices"))==1
            def submit(work):
                return call("POST",f"/api/devices/{device}/jobs",work)["jobId"]
            def wait(jid):
                deadline=time.time()+20
                while time.time()<deadline:
                    j=call("GET","/api/jobs/"+jid)
                    if j["status"]!="running": return j
                    time.sleep(.1)
                raise AssertionError("Job stalled")
            def run(work,ok=True):
                j=wait(submit(work))
                assert j["status"]=="completed",j
                assert j["result"]["ok"]==ok,j
                return j["result"]
            expect(400,"POST",f"/api/devices/{device}/jobs",{"op":"invalid"})
            expect(400,"POST",f"/api/devices/{device}/jobs",{"op":"exec","timeoutSeconds":0})
            r=run({"op":"exec","command":"Write-Output 'jedar-bridge-ok'"})
            assert "jedar-bridge-ok" in r["stdout"]["text"],r
            r=run({"op":"exec","command":"[Console]::Error.WriteLine('expected'); exit 7"},False)
            assert r["exitCode"]==7
            path=str(pathlib.Path(tmp)/"sample.txt")
            payload=base64.b64encode("hello مرحبا".encode()).decode()
            run({"op":"write","path":path,"contentBase64":payload})
            run({"op":"write","path":path,"contentBase64":payload},False)
            assert run({"op":"read","path":path})["contentBase64"]==payload
            def wrapper(*args):
                result = subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT/"scripts/bridge.ps1"),
                    "-Server", URL, "-TokenFile", str(pathlib.Path(tmp)/"data/operator-token.txt"),
                    "-Device", device, *args], capture_output=True, text=True)
                assert result.returncode == 0, result.stderr
                return json.loads(result.stdout)
            assert wrapper("-Action","exec","-Command","Write-Output wrapper-ok")["result"]["ok"]
            download = pathlib.Path(tmp)/"downloaded.txt"
            wrapper("-Action","read","-Path",path,"-LocalFile",str(download))
            assert download.read_bytes()==base64.b64decode(payload)
            assert any(x["path"]==path for x in run({"op":"list","path":tmp})["entries"])
            run({"op":"write","path":path,"contentBase64":payload,"overwrite":True})
            run({"op":"delete","path":path})
            assert not pathlib.Path(path).exists()
            jid=submit({"op":"exec","command":"Start-Sleep 20","timeoutSeconds":1})
            expect(409,"POST",f"/api/devices/{device}/jobs",{"op":"exec","command":"hostname"})
            assert wait(jid)["result"]["timedOut"]
            r=run({"op":"exec","command":"'x' * 210000"})
            assert r["stdout"]["truncated"]
            # Revocation disconnects an in-flight process and marks its job disconnected.
            jid=submit({"op":"exec","command":"Start-Sleep 60"})
            call("DELETE",f"/api/devices/{device}")
            assert wait(jid)["status"]=="disconnected"
            for _ in range(30):
                if not call("GET","/api/devices"): break
                time.sleep(.1)
            assert not call("GET","/api/devices")
            print("PASS: authentication, pairing/reuse, exec/exit code, file CRUD/overwrite, operator wrapper, validation, busy conflict, timeout, output cap, revocation.")
        finally:
            if client:
                if client.poll() is None:
                    try: client.stdin.write("\n"); client.stdin.flush(); client.wait(timeout=5)
                    except Exception: client.kill()
                client.wait()
                client.stdin.close(); client.stdout.close()
            server.terminate(); server.wait(timeout=10); log.close()
if __name__=="__main__": main()
