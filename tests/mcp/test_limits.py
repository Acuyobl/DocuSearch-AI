"""Validación de límites y errores seguros."""
from __future__ import annotations


def test_invalid_search_query_is_rejected(stdio):
    result = stdio.call("search_documents", {"query": "   ", "limit": 2})
    assert result.get("error") == "invalid_request"


def test_page_zero_is_rejected(stdio, sample_document):
    result = stdio.call("get_document_page", {
        "documentCode": sample_document["internalCode"], "page": 0,
    })
    assert result.get("error") == "invalid_request"


def test_unknown_document_does_not_leak(stdio):
    result = stdio.call("get_document_page", {"documentCode": "NO-EXISTE-0000", "page": 1})
    assert result.get("error") == "not_authorized"
