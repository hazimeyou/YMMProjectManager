"""Serve a read-only synthetic Git remote with TLS on a literal loopback IP."""

import http.server
import os
import pathlib
import socket
import ssl
import subprocess
import sys
import threading
import urllib.parse


root, certificate, key, mode_file, ready_file, access_file, git = sys.argv[1:]


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        if (url.path, url.query) != ("/fixture.git/info/refs", "service=git-upload-pack"):
            self.send_error(404)
            return
        self.serve_git(url)

    def do_POST(self):
        url = urllib.parse.urlsplit(self.path)
        if url.path != "/fixture.git/git-upload-pack" or url.query:
            self.send_error(404)
            return
        self.serve_git(url)

    def serve_git(self, url):
        mode = pathlib.Path(mode_file).read_text(encoding="utf-8").strip()
        with open(access_file, "a", encoding="utf-8") as access:
            access.write(f"{mode} {self.command} {url.path}\n")
        if mode == "disconnect":
            self.connection.shutdown(socket.SHUT_RDWR)
            self.close_connection = True
            return
        if mode == "timeout":
            threading.Event().wait(20)
            self.close_connection = True
            return
        if mode == "error":
            self.send_error(503)
            return
        if mode != "normal":
            self.send_error(500)
            return

        length = int(self.headers.get("Content-Length", "0"))
        if not 0 <= length <= 1024 * 1024 or self.headers.get("Transfer-Encoding"):
            self.send_error(413)
            return
        environment = {k: v for k, v in os.environ.items() if not k.upper().startswith("GIT_")}
        environment.update(
            GIT_CONFIG_GLOBAL="NUL",
            GIT_CONFIG_NOSYSTEM="1",
            GIT_ATTR_NOSYSTEM="1",
            GIT_TERMINAL_PROMPT="0",
            GIT_ALLOW_PROTOCOL="",
            GIT_NO_LAZY_FETCH="1",
            GIT_PROJECT_ROOT=root,
            GIT_HTTP_EXPORT_ALL="1",
            PATH_INFO=url.path,
            QUERY_STRING=url.query,
            REQUEST_METHOD=self.command,
            CONTENT_TYPE=self.headers.get("Content-Type", ""),
            CONTENT_LENGTH=str(length),
            HTTP_GIT_PROTOCOL=self.headers.get("Git-Protocol", ""),
        )
        result = subprocess.run(
            [git, "http-backend"],
            input=self.rfile.read(length),
            capture_output=True,
            cwd=root,
            env=environment,
            timeout=10,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=True,
        )
        headers, separator, content = result.stdout.partition(b"\r\n\r\n")
        if not separator:
            self.send_error(502)
            return
        response = [header.decode("ascii").split(": ", 1) for header in headers.split(b"\r\n")]
        status = next((value for name, value in response if name.lower() == "status"), "200")
        self.send_response(int(status.split()[0]))
        for name, value in response:
            if name.lower() not in ("status", "content-length"):
                self.send_header(name, value)
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)


context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.minimum_version = ssl.TLSVersion.TLSv1_2
context.load_cert_chain(certificate, key)


class FixtureServer(http.server.ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        if not isinstance(sys.exc_info()[1], (ConnectionResetError, BrokenPipeError)):
            super().handle_error(request, client_address)


with FixtureServer(("127.0.0.1", 0), Handler) as server:
    server.socket = context.wrap_socket(server.socket, server_side=True)
    pathlib.Path(ready_file).write_text(str(server.server_port), encoding="ascii")
    server.serve_forever()
