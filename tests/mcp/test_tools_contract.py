"""Contrato funcional de las tools."""
from __future__ import annotations

import os


def test_list_documents_contract(stdio):
    documents = stdio.call("list_documents", {"limit": 3})
    assert isinstance(documents, list)
    assert len(documents) <= 3
    for document in documents:
        assert document["internalCode"]
        assert document["title"]
        assert document["confidentiality"] in ("Público", "Uso Interno", "Confidencial", "Estrictamente Secreto")
        assert "text" not in document


def test_search_documents_contract(stdio):
    query = os.environ.get("MCP_SEARCH_QUERY", "Mesopotamia")
    results = stdio.call("search_documents", {"query": query, "limit": 3})
    assert isinstance(results, list)
    for hit in results:
        assert hit["internalCode"]
        assert hit["pageNumber"] >= 1
        assert len(hit["snippet"]) <= 500


def test_get_document_page_contract(stdio, sample_document):
    page = stdio.call("get_document_page", {
        "documentCode": sample_document["internalCode"], "page": 1,
    })
    assert page["internalCode"] == sample_document["internalCode"]
    assert page["pageNumber"] == 1
    assert page["text"]


def test_rag_query_is_extractive(stdio):
    answer = stdio.call("rag_query", {"question": "¿Cuántos documentos hay?"})
    assert "text" in answer
    assert "sources" in answer
    assert "generated" not in answer.get("text", "").lower()
