# Contribuir a DocuSearch AI

Gracias por tu interés. Este documento resume lo mínimo necesario para compilar,
probar y enviar cambios.

## Requisitos

- **.NET SDK 10** (línea 10.0.x). El proyecto no usa Node.js ni npm.
- **SQL Server Express** solo si vas a ejecutar la aplicación o los checks de
  integración con base de datos. La compilación y los checks básicos no lo
  necesitan.

## Compilar

```powershell
dotnet restore DocuSearch.sln
dotnet build DocuSearch.sln -c Release
```

Para ejecutar la aplicación en local:

```powershell
dotnet run --project DocuSearch.csproj
```

La cadena de conexión y el arranque se detallan en el [README](README.md).

## Ejecutar los checks

```powershell
dotnet run --project tests/DocuSearch.Checks.csproj
```

Los checks de integración con SQL Server requieren `--database` y una base
exclusiva terminada en `_tests`:

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=...;Database=DocuSearch_tests;..."
dotnet run --project tests/DocuSearch.Checks.csproj -- --database
```

En CI solo se ejecutan los checks que no necesitan base de datos.

## Estilo

- Respeta el [`.editorconfig`](.editorconfig): UTF-8, 4 espacios en C# y
  `.razor`/`.cshtml`, 2 espacios en JSON/YAML, nueva línea final.
- Usa `Nullable` e `ImplicitUsings` (ya activados) y mantén los mensajes y la
  documentación en **español**, con tono sobrio.
- No dejes código comentado ni dependencias sin uso.
- Texto y prompts: guarda los ficheros en **UTF-8 sin BOM**; los finales de
  línea los normaliza `.gitattributes`.

## Reglas para pull requests

- No añadas a Git artefactos de compilación (`bin/`, `obj/`), logs,
  `appsettings.Local.json` ni secretos.
- **No se aceptan PRs que incluyan binarios nuevos de más de 1 MB** (`.docx`,
  `.pdf`, imágenes, `.zip`, etc.). Si el cambio requiere un binario mayor,
  abre antes una discusión para acordar la alternativa.
- Un PR debe compilar y pasar los checks de CI antes de revisarse.
- Describe el cambio, el motivo y cómo lo has verificado; mantén el alcance
  acotado.
