"""Microservicio local de reranking cross-encoder (FastEmbed/ONNX).

POST /rerank
{"query": "...", "documents": ["...", "..."]}
-> {"scores": [4.1, -10.8]}

Uso:
  python eval/reranker_service.py --port 4098 --model BAAI/bge-reranker-v2-m3-int8
"""
from __future__ import annotations

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from fastembed.rerank.cross_encoder import TextCrossEncoder

_model = None
_model_name = "BAAI/bge-reranker-v2-m3-int8"


def get_model():
    global _model
    if _model is None:
        _model = TextCrossEncoder(_model_name)
    return _model


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/rerank":
            self.send_error(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8"))
        query = body.get("query", "")
        documents = body.get("documents", [])
        if not isinstance(query, str) or not isinstance(documents, list):
            self.send_error(400, "query/documents inválidos")
            return
        scores = [float(score) for score in get_model().rerank(query, documents)]
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
    parser.add_argument("--port", type=int, default=4098)
    parser.add_argument("--model", default=_model_name)
    args = parser.parse_args()
    _model_name = args.model
    print(f"reranker cross-encoder escuchando en http://127.0.0.1:{args.port} ({_model_name})", flush=True)
    ThreadingHTTPServer(("0.0.0.0", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
