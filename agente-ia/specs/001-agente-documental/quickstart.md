# Inicio rápido del agente

**Referencia:** [AGENTE-001](spec.md) · **Entorno:** Windows/PowerShell.

Ejecutar los comandos desde la raíz del proyecto, no desde esta carpeta de specs.
El modo LangChain/Ollama no requiere Node.js, npm, Docker ni OpenCode. El motor OpenCode
es opcional y se ejecuta como servicio externo (`opencode serve`).

## 1. Requisitos

- SDK .NET 10; `dotnet --version`.
- Python 3.14 instalado y launcher `py`; `py --list-paths`.
- SQL Server Express accesible con la identidad Windows del proceso.
- Ollama abierto, API en `http://127.0.0.1:11434` y modelo local
  `qwen2.5-coder:7b` ya descargado.
- Acceso a NuGet/PyPI para restaurar dependencias cuando corresponda.
  Las consultas y documentos no deben enviarse a esos servicios.

Preparar otro equipo requiere instalar esos prerrequisitos. Este paquete no incluye
el intérprete Python, los pesos del modelo ni una copia de la base.

## 2. Configuración

En la [configuración de aplicación](../../../appsettings.json):

```json
{
  "ConnectionStrings": {
    "Documents": "Server=ICEPALFLOR\\SQLEXPRESS;Database=DocuSearch;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
  },
  "AllowedHosts": "localhost;127.0.0.1;[::1]",
  "Ollama": {
    "Enabled": true,
    "UseLangChain": true,
    "BaseUrl": "http://127.0.0.1:11434",
    "Model": "qwen2.5-coder:7b",
    "TimeoutSeconds": 240
  }
}
```

El ejemplo muestra las secciones relevantes, no obliga a reemplazar el archivo completo.
En otro equipo cambia el servidor. `TrustServerCertificate=True` se utiliza para la
instancia local con certificado autofirmado, no como recomendación de producción.
No agregues claves cloud ni publiques credenciales.

`PythonExecutable` es opcional: su valor predeterminado es
`%LOCALAPPDATA%\DocuSearch\LangChain\.venv\Scripts\python.exe`.
`UseLangChain=false` selecciona el motor C# directo; `Enabled=false` selecciona
solo extracción. Ninguno es un fallback automático ante errores.

## 3. Preparación inicial

```powershell
dotnet restore
.\LangChain\setup.ps1
```

El script crea un entorno virtual externo en
`%LOCALAPPDATA%\DocuSearch\LangChain\.venv`, instala los paquetes declarados y
ejecuta `worker.py --check`. No instala Node.js ni descarga modelos.

Para preparar esquema nuevo o pendiente:

```powershell
dotnet run -- --initialize-database
```

Aplica migraciones y conserva datos existentes; no ejecutar un restablecimiento
de ejemplos como sustituto. Respalda previamente una base con información importante.
La identidad necesita los permisos apropiados; no concedas administrador SQL permanente.

## 4. Desarrollo o publicación: elegir uno

### Desarrollo

```powershell
dotnet run
```

Abrir <http://localhost:5068/agent>. No iniciar otra instancia en el mismo puerto.

### Publicación local

```powershell
dotnet publish .\DocuSearch.csproj -c Release --no-restore
Set-Location -LiteralPath '.\bin\Release\net10.0\publish'
dotnet .\DocuSearch.dll --urls http://localhost:5068
```

Ejecutar desde la carpeta publicada permite resolver sus recursos y configuración.
El worker se copia a la publicación; el entorno virtual permanece fuera.
Deja la consola abierta mientras uses la app. Para detener esta instancia usa
Ctrl+C en esa consola; no termines procesos ajenos por nombre.
Si una publicación anterior está ejecutándose, detén solo esa instancia antes de
sobrescribir archivos; las métricas y documentos permanecen en SQL Server.

## 5. Comprobar dependencias

```powershell
Invoke-RestMethod 'http://localhost:5068/health'
Invoke-RestMethod 'http://127.0.0.1:11434/api/tags'
& "$env:LOCALAPPDATA\DocuSearch\LangChain\.venv\Scripts\python.exe" -I .\LangChain\worker.py --check
```

El último comando supone estar nuevamente en la raíz del proyecto.
`/health` esperado: `status=ready`, `database=SQL Server`,
`assistant=langchain-ollama-local`. Esto verifica SQL y describe la configuración;
no confirma una generación. En el asistente, **Comprobar conexión** verifica
modelo e importación del worker sin enviar documentos.

## 6. Prueba manual funcional

1. Entra con un perfil autorizado. Recuerda que es una demostración de permisos.
2. En **Documentos**, carga un PDF sintético con texto y confirma nombre, metadatos
   y procesamiento. Espera la confirmación de guardado.
3. Si permanecías en otra pestaña, pulsa **Actualizar documentos**.
4. En el chat usa, por ejemplo, `Resume POL-2025-0012` para un ejemplo accesible.
5. Comprueba que el editor queda vacío durante la espera y que una pregunta nueva
   escrita como borrador no desaparece cuando llega la respuesta.
6. Comprueba la etiqueta `LangChain · Ollama local` y abre una cita por página.
7. Prueba `Lista documentos de uso interno`: debe responder por extracción.
8. Con administración general o auditoría, abre
   <http://localhost:5068/observability> y pulsa **Actualizar métricas**.
9. Verifica duración y tokens disponibles, sin texto de consulta en la traza.
10. Como consultor, la ruta de observabilidad debe mostrar acceso restringido.

No elimines documentos del usuario para validar el flujo. Utiliza material de prueba
identificable y borra únicamente ese material si necesitas limpiar.

## 7. Pruebas automatizadas

Desde la raíz:

```powershell
dotnet build .\DocuSearch.csproj
dotnet run --project .\tests\DocuSearch.Checks.csproj
& "$env:LOCALAPPDATA\DocuSearch\LangChain\.venv\Scripts\python.exe" -m unittest discover -s .\LangChain -p "test_*.py" -v
```

Añadir pruebas reales del worker y modelo con fuente sintética:

```powershell
dotnet run --project .\tests\DocuSearch.Checks.csproj -- --langchain
```

Integración SQL **solo contra una base exclusiva y desechable**:

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=ICEPALFLOR\SQLEXPRESS;Database=DocuSearch_tests;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
dotnet run --project .\tests\DocuSearch.Checks.csproj -- --database --langchain
```

**Nunca apuntes estas pruebas a `DocuSearch` ni a una base que quieras conservar.**
La suite exige sufijo `_tests`, inicializa y restablece datos de prueba.
Línea base verificada antes de crear estos documentos: 99 comprobaciones .NET con
SQL/LangChain y ocho pruebas Python correctas.

## 8. Resolución de problemas

| Síntoma | Acción |
|---|---|
| Conexión rechazada en 5068 | Verificar que la DLL/app esté ejecutándose y que no haya terminado la consola |
| Pantalla con datos anteriores | Actualizar documentos; no volver a cargar sin comprobar si ya existe |
| PDF no seleccionado | Revisar la cola; el selector de la vista previa puede requerir Edge/Chrome |
| Tabla `AgentTraces` ausente | Aplicar migraciones con `--initialize-database`, sin resetear datos |
| Python/worker no encontrado | Ejecutar setup y comprobar publicación/ruta `PythonExecutable` |
| LangChain no importa | Revisar resultado del setup y `python -m pip check` en el entorno virtual |
| Modelo no disponible | Abrir Ollama y comprobar que el modelo configurado esté instalado localmente |
| Timeout de generación | Comprobar memoria y carga inicial; reducir la pregunta. No se usa nube como respaldo |
| Sin fuentes | Probar código o términos más precisos y revisar permisos/texto extraído |
| Sin tokens | Puede ser ruta extractiva, error o falta de contadores; no se estiman |
| Panel vacío | Se necesitan llamadas al agente que terminen; un check de conexión no crea trazas |
| Error al guardar observabilidad | Revisar SQL, permisos y esquema; no interpretar la consulta como completada |

No expongas esta demo mediante túneles, proxy público o red hasta tener
autenticación real, TLS y una política de despliegue.
