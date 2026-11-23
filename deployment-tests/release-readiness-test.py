"""Exercise the published Pi web application on the local .NET runtime."""

import http.client
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unittest
import xml.etree.ElementTree as ET
from contextlib import contextmanager


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ("index.html", "tv.html", "css/app.css", "js/app.js", "js/snapshots.js", "js/signalr.min.js")


class PublishedRelease(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        directory = tempfile.TemporaryDirectory(prefix="ltar-release-")
        cls.addClassCleanup(directory.cleanup)
        cls.work = Path(directory.name)
        if existing := os.environ.get("LTAR_TEST_PUBLISH"):
            cls.release = Path(existing).resolve()
            return
        cls.release = cls.work / "publish"
        subprocess.run(
            ["dotnet", "publish", "LazerTagWebServer/LazerTagWebServer.csproj",
             "-c", "Release", "--no-restore", "--no-self-contained", "-o", str(cls.release)],
            cwd=ROOT, check=True,
        )

    @contextmanager
    def server(self, release=None, settings=None):
        release = release or self.release
        log = self.work / (release.name + ".log")
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Production",
                   Arena__OperatorPin=str(10000000 + secrets.randbelow(90000000)),
                   Arena__DisableRelayReconnect="true", Arena__DataDirectory=str(self.work / (release.name + "-data")))
        # The Pi logs to stdout. Local test processes need no Windows Event Log access.
        env["Logging__EventLog__LogLevel__Default"] = "None"
        env["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information"
        env.update(settings or {})
        command = [str(release / "LazerTagWebServer")] if os.environ.get("LTAR_TEST_PUBLISH") else ["dotnet", str(release / "LazerTagWebServer.dll")]
        with log.open("wb") as output:
            process = subprocess.Popen(
                [*command, "--urls", "http://127.0.0.1:0"],
                cwd=self.work, env=env, stdout=output, stderr=subprocess.STDOUT,
            )
            try:
                deadline = time.monotonic() + 15
                while process.poll() is None and time.monotonic() < deadline:
                    match = re.search(rb"Now listening on: http://127\.0\.0\.1:(\d+)", log.read_bytes())
                    if match:
                        yield int(match[1])
                        return
                    time.sleep(0.05)
                self.fail("Server failed to listen. " + log.read_text(errors="replace"))
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()

    def get(self, port, path):
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
        try:
            connection.request("GET", path)
            response = connection.getresponse()
            return response.status, response.read()
        finally:
            connection.close()

    @unittest.skipUnless(sys.platform.startswith("linux"), "requires the Pi's Linux flock semantics")
    def test_match_and_linux_deployer_share_the_same_lock(self):
        import fcntl
        lock = self.work / "match.lock"
        lock.touch()
        pin = str(10000000 + secrets.randbelow(90000000))
        with socket.create_server(("127.0.0.1", 0)) as listener:
            listener.settimeout(10)
            settings = dict(Arena__OperatorPin=pin, Arena__UpdateLockFile=str(lock),
                            Arena__RelayAddress="127.0.0.1:" + str(listener.getsockname()[1]))
            with self.server(settings=settings) as port, lock.open("rb") as guard:
                def post(route, body=None, token=None):
                    conn = http.client.HTTPConnection("127.0.0.1", port, timeout=15)
                    try:
                        headers = {"Content-Type": "application/json"}
                        if token:
                            headers["Authorization"] = "Bearer " + token
                        conn.request("POST", route, json.dumps(body), headers)
                        response = conn.getresponse()
                        return response.status, json.loads(response.read())
                    finally:
                        conn.close()

                # A custom test inode must not advertise compatibility with the
                # system installer's fixed /run/lock location.
                self.assertFalse(json.loads(self.get(port, "/healthz/live")[1])["matchUpdateLock"])
                status, session = post("/api/operator/login", {"pin": pin})
                self.assertEqual(200, status)
                token = session["token"]
                setup = {"players": [{"slot": 1}, {"slot": 2}]}
                fcntl.flock(guard, fcntl.LOCK_EX | fcntl.LOCK_NB)
                self.assertEqual(409, post("/api/game/start", setup, token)[0])
                fcntl.flock(guard, fcntl.LOCK_UN)
                with ThreadPoolExecutor(1) as executor:
                    start = executor.submit(post, "/api/game/start", setup, token)
                    peer, _ = listener.accept()
                    with peer, peer.makefile("rb") as reader:
                        self.assertEqual(b"HELLO 1\r\n", reader.readline())
                        self.assertEqual(1, subprocess.run(["flock", "-n", str(lock), "true"]).returncode)
                        peer.sendall(b"READY 1\r\n")
                        self.assertEqual(200, start.result()[0])
                        self.assertEqual(1, subprocess.run(["flock", "-n", str(lock), "true"]).returncode)
                        self.assertEqual(200, post("/api/game/endgame", token=token)[0])
                        self.assertEqual(0, subprocess.run(["flock", "-n", str(lock), "true"]).returncode)

    def test_ready_offline_with_working_defaults_and_displays(self):
        with self.server() as port:
            status, body = self.get(port, "/healthz")
            self.assertEqual(200, status)
            self.assertFalse(json.loads(body)["relayConnected"])
            self.assertEqual(200, self.get(port, "/healthz/live")[0])
            status, modes = self.get(port, "/api/game/gametypes")
            self.assertEqual(200, status)
            for mode in json.loads(modes):
                self.assertEqual(200, self.get(port, f"/api/game/gametypedefaults/{mode['id']}")[0])
            for asset in ASSETS:
                self.assertEqual(200, self.get(port, "/" + asset)[0])

    def assert_unready(self, relative_path, replacement=None):
        path = self.release / relative_path
        original = path.read_bytes()
        try:
            if replacement is None:
                path.unlink()
            else:
                path.write_bytes(replacement)
            with self.server() as port:
                self.assertEqual(200, self.get(port, "/healthz/live")[0])
                status, body = self.get(port, "/healthz")
                self.assertEqual(503, status)
                self.assertFalse(json.loads(body)["relayConnected"])
        finally:
            path.write_bytes(original)

    def test_unusable_catalog(self):
        root = ET.parse(self.release / "GameTypes.xml").getroot()
        modes = root.find("GameTypes")
        modes.remove(modes.find("GameType[@Name='2TMS']"))
        for problem, replacement in (("missing", None), ("malformed", b"<"),
                                     ("missing default mode", ET.tostring(root))):
            with self.subTest(problem=problem):
                self.assert_unready("GameTypes.xml", replacement)

    def test_missing_operator_or_tv_asset(self):
        for path in ASSETS:
            with self.subTest(path=path):
                self.assert_unready("wwwroot/" + path)
        self.assert_unready("wwwroot/tv.html", b"")

    def test_deployer_restores_previous_release_after_failed_probe(self):
        sandbox = self.work / "deployment"
        old = sandbox / "releases" / "release-good"
        candidate = old.with_name("release-candidate")
        shutil.copytree(self.release, old)
        # The staged layout uses the Pi apphost filename. Both test processes
        # run the published DLL through the local runtime.
        if os.name == "nt":
            shutil.copyfile(old / "LazerTagWebServer.exe", old / "LazerTagWebServer")
        shutil.copytree(old, candidate)
        (candidate / "wwwroot/tv.html").unlink()
        bash = "bash" if os.name != "nt" else str(Path(os.environ["ProgramFiles"]) / "Git/bin/bash.exe")
        env = dict(os.environ, TEST_PYTHON=sys.executable, NO_PROXY="127.0.0.1", no_proxy="127.0.0.1")
        if os.name == "nt":
            env["MSYS"] = "winsymlinks:sys"
        with self.server(old) as good_port, self.server(candidate) as bad_port:
            subprocess.run([bash, str(ROOT / "deployment-tests/deploy-release-test.sh"),
                            str(sandbox), str(good_port), str(bad_port)],
                           cwd=ROOT, env=env, timeout=20, check=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
