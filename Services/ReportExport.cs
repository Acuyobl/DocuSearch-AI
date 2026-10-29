using System.Net;
using System.Text;
using DocuSearch.Models;

namespace DocuSearch.Services;

public static class ReportExport
{
    public static string CsvCell(string? value)
    {
        value ??= "";
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static byte[] Csv(IEnumerable<string[]> rows) => Encoding.UTF8.GetPreamble()
        .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(CsvCell)))))).ToArray();
    public static byte[] CatalogCsv(IEnumerable<DocumentItem> documents) => Csv(
        new[] { new[] { "Código", "Título", "Autor", "Departamento", "Categoría", "Confidencialidad", "Versión", "Fecha", "Páginas", "Estado" } }
        .Concat(documents.Select(d => new[] { d.InternalCode, d.Title, d.Author, d.Department, d.Category, d.Confidentiality,
            d.Version, d.UploadDate.ToString("O"), d.PageCount.ToString(), d.OcrStatus })));
    public static byte[] AuditCsv(IEnumerable<AuditEvent> events) => Csv(
        new[] { new[] { "UTC", "Usuario", "Rol", "Acción", "Expediente", "Detalle", "Estado" } }
        .Concat(events.Select(a => new[] { a.TimestampUTC.ToString("O"), a.UserName, a.UserRole, a.Action, a.DocumentCode, a.Details, a.Status })));

    public static byte[] Html(IEnumerable<DocumentItem> documents, string author)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var rows = documents.Select(d => $"<tr><td>{E(d.InternalCode)}</td><td>{E(d.Title)}</td><td>{E(d.Department)}</td><td>{E(d.Confidentiality)}</td><td>{d.PageCount}</td></tr>");
        var html = $$"""
            <!doctype html><html lang="es"><meta charset="utf-8"><title>Informe ejecutivo DocuSearch</title>
            <style>body{font:14px system-ui;margin:40px;color:#172033}table{border-collapse:collapse;width:100%}td,th{padding:10px;border:1px solid #ccc;text-align:left}h1{color:#4338ca}@media print{body{margin:0} }</style>
            <h1>Informe ejecutivo de control documental</h1><p>Generado por {{E(author)}} · {{DateTimeOffset.UtcNow:O}}</p>
            <p>Solo se incluyen los expedientes accesibles al perfil que genera el informe.</p>
            <table><thead><tr><th>Código</th><th>Expediente</th><th>Departamento</th><th>Clasificación</th><th>Páginas</th></tr></thead><tbody>
            {{string.Join("", rows)}}</tbody></table><p>Responsable: _____________________ Firma: _____________________</p></html>
            """;
        return Encoding.UTF8.GetBytes(html);
    }
}
