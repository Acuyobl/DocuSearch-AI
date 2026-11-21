"""Cliente mínimo y sin dependencias externas para el servidor MCP de DocuSearch."""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import struct
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar
from typing import Any


class McpError(RuntimeError):
    pass


def _sse_json(body: str) -> dict[str, Any]:
    data = [line[6:] for line in body.splitlines() if line.startswith("data: ")]
    if not data:
        return json.loads(body)
    return json.loads(data[-1])


class McpHttpClient:
    def __init__(self, url: str, token: str | None, timeout: float = 30):
        self.url = url
        self.token = token
        self.timeout = timeout

    def _post(self, payload: dict[str, Any], token: str | None = None) -> tuple[int, dict[str, Any]]:
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(
            self.url,
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            method="POST",
            headers=headers,
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.status, _sse_json(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            try:
                return error.code, _sse_json(body)
            except Exception:
                return error.code, {"error": body}

    def _notify(self, payload: dict[str, Any], token: str | None = None) -> None:
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(
            self.url,
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            method="POST",
            headers=headers,
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout):
                pass
        except urllib.error.HTTPError:
            # Una notificación no devuelve respuesta JSON; 202/204 son normales.
            pass

    def initialize(self, token: str | None = None) -> dict[str, Any]:
        token = self.token if token is None else token
        status, result = self._post(
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "docusearch-pytest", "version": "1.0"},
                },
            },
            token,
        )
        if status != 200 or "result" not in result:
            raise McpError(f"initialize falló: HTTP {status} {result}")
        self._notify({"jsonrpc": "2.0", "method": "notifications/initialized"}, token)
        return result["result"]

    def list_tools(self, token: str | None = None) -> list[dict[str, Any]]:
        token = self.token if token is None else token
        status, result = self._post(
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}, token)
        if status != 200:
            raise McpError(f"tools/list falló: HTTP {status}")
        return result.get("result", {}).get("tools", [])

    def call(self, name: str, arguments: dict[str, Any] | None = None,
             token: str | None = None, request_id: int = 3) -> dict[str, Any]:
        token = self.token if token is None else token
        status, result = self._post({
            "jsonrpc": "2.0", "id": request_id, "method": "tools/call",
            "params": {"name": name, "arguments": arguments or {}},
        }, token)
        if status != 200:
            raise McpError(f"{name} falló: HTTP {status} {result}")
        content = result.get("result", {}).get("content", [])
        if not content:
            raise McpError(f"{name} no devolvió contenido: {result}")
        try:
            return json.loads(content[0].get("text", "null"))
        except json.JSONDecodeError:
            return {"text": content[0].get("text", "")}


class McpStdioClient:
    def __init__(self, connection: str, user: str, dll: str, cwd: str, timeout: float = 60):
        env = os.environ.copy()
        env["DOCUSEARCH_MCP_CONNECTION"] = connection
        env["DOCUSEARCH_MCP_USER"] = user
        self.timeout = timeout
        self.process = subprocess.Popen(
            ["dotnet", dll],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            bufsize=1,
            cwd=cwd,
            env=env,
        )
        self._id = 0

    def _send(self, payload: dict[str, Any]) -> dict[str, Any]:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        assert self.process.stdout is not None
        line = self.process.stdout.readline()
        if not line:
            raise McpError("El servidor MCP stdio cerró la salida")
        return json.loads(line)

    def _notify(self, payload: dict[str, Any]) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self.process.stdin.flush()

    def initialize(self) -> dict[str, Any]:
        self._id += 1
        result = self._send({
            "jsonrpc": "2.0", "id": self._id, "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "docusearch-pytest", "version": "1.0"},
            },
        })
        self._notify({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return result.get("result", {})

    def list_tools(self) -> list[dict[str, Any]]:
        self._id += 1
        result = self._send({"jsonrpc": "2.0", "id": self._id, "method": "tools/list", "params": {}})
        return result.get("result", {}).get("tools", [])

    def call(self, name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
        self._id += 1
        result = self._send({
            "jsonrpc": "2.0", "id": self._id, "method": "tools/call",
            "params": {"name": name, "arguments": arguments or {}},
        })
        content = result.get("result", {}).get("content", [])
        if not content:
            raise McpError(f"{name} no devolvió contenido: {result}")
        try:
            return json.loads(content[0].get("text", "null"))
        except json.JSONDecodeError:
            return {"text": content[0].get("text", "")}

    def close(self) -> None:
        try:
            self.process.stdin.close()
            self.process.wait(timeout=5)
        except Exception:
            self.process.kill()


class AdminApi:
    """Login TOTP + endpoints administrativos de tokens, solo para bases de prueba."""

    def __init__(self, base_url: str, email: str, totp_secret: str):
        self.base_url = base_url.rstrip("/")
        self.email = email
        self.totp_secret = totp_secret
        self.jar = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    @staticmethod
    def _base32(value: str) -> bytes:
        alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
        clean = value.strip().replace("=", "").upper()
        bits = "".join(f"{alphabet.index(char):05b}" for char in clean)
        return bytes(int(bits[index:index + 8], 2) for index in range(0, len(bits) - 7, 8))

    def _totp(self) -> str:
        counter = int(time.time()) // 30
        digest = hmac.new(self._base32(self.totp_secret),
                          struct.pack(">Q", counter), hashlib.sha1).digest()
        offset = digest[-1] & 0x0F
        return f"{(struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000:06d}"

    def login(self) -> None:
        with self.opener.open(f"{self.base_url}/login") as response:
            html = response.read().decode("utf-8")
        marker = 'name="__RequestVerificationToken" value="'
        start = html.index(marker) + len(marker)
        token = html[start:html.index('"', start)]
        data = urllib.parse.urlencode({
            "__RequestVerificationToken": token,
            "email": self.email,
            "code": self._totp(),
            "returnUrl": "/",
        }).encode()
        request = urllib.request.Request(f"{self.base_url}/account/login", data=data, method="POST")
        self.opener.open(request)

    def create_token(self, name: str = "pytest", valid_days: int = 1) -> dict[str, Any]:
        payload = json.dumps({"name": name, "validDays": valid_days}).encode()
        request = urllib.request.Request(
            f"{self.base_url}/api/mcp/tokens", data=payload, method="POST",
            headers={"Content-Type": "application/json"})
        with self.opener.open(request) as response:
            return json.loads(response.read().decode("utf-8"))

    def revoke_token(self, token_id: str) -> None:
        request = urllib.request.Request(
            f"{self.base_url}/api/mcp/tokens/{token_id}", method="DELETE")
        self.opener.open(request).read()

    def audit_html(self) -> str:
        with self.opener.open(f"{self.base_url}/audit") as response:
            return response.read().decode("utf-8", errors="replace")
