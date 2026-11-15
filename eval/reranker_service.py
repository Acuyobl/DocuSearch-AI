"""Microservicio local de reranking cross-encoder (FastEmbed/ONNX).

POST /rerank
{"query": "...", "documents": ["...", "..."]}
-> {"scores": [0.98, 0.00002]}   (sigmoide de los logits, en [0, 1])

Uso:
  python eval/reranker_service.py --port 4098 --model BAAI/bge-reranker-v2-m3-int8

Escucha solo en loopback por defecto. Exponerlo en otra interfaz (--host 0.0.0.0)
publica sin autenticación un servicio que recibe texto de los expedientes.
"""
from __future__ import annotations

import argparse
import json
import math
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from fastembed.rerank.cross_encoder import TextCrossEncoder

MAX_BODY_BYTES = 1_000_000
MAX_DOCUMENTS = 100

_model = None
_model_lock = threading.Lock()
_model_name = "BAAI/bge-reranker-v2-m3-int8"


def get_model():
    global _model
    if _model is None:
        with _model_lock:
            if _model is None:
                _model = TextCrossEncoder(_model_name)
    return _model


def _sigmoid(value: float) -> float:
    if value >= 0:
        return 1.0 / (1.0 + math.exp(-value))
    exp = math.exp(value)
    return exp / (1.0 + exp)


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/rerank":
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            self.send_error(400, "Content-Length invalido")
            return
        if length <= 0 or length > MAX_BODY_BYTES:
            self.send_error(413 if length > MAX_BODY_BYTES else 400, "Cuerpo vacio o demasiado grande")
            return
        try:
            body = json.loads(self.rfile.read(length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            self.send_error(400, "JSON invalido")
            return
        query = body.get("query") if isinstance(body, dict) else None
        documents = body.get("documents") if isinstance(body, dict) else None
        if (not isinstance(query, str) or not query.strip() or not isinstance(documents, list)
                or len(documents) > MAX_DOCUMENTS or not all(isinstance(doc, str) for doc in documents)):
            self.send_error(400, f"query (texto) y documents (lista de hasta {MAX_DOCUMENTS} textos) requeridos")
            return
        # El cross-encoder devuelve logits; la sigmoide los lleva a [0, 1] sin alterar el orden.
        scores = [_sigmoid(float(score)) for score in get_model().rerank(query, documents)] if documents else []
        payload = json.dumps({"scores": scores}).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args):
        pass


def main() -> None:
    global _model_name
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1",
                        help="Interfaz de escucha (por defecto solo loopback)")
    parser.add_argument("--port", type=int, default=4098)
    parser.add_argument("--model", default=_model_name)
    args = parser.parse_args()
    _model_name = args.model
    if args.host not in ("127.0.0.1", "localhost", "::1"):
        print(f"AVISO: escuchando en {args.host} sin autenticación; el texto de los expedientes "
              "quedará accesible desde la red.", flush=True)
    print(f"reranker cross-encoder escuchando en http://{args.host}:{args.port} ({_model_name})", flush=True)
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
