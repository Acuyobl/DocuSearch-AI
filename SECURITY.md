# Seguridad

## Versiones soportadas

DocuSearch AI es una **demo funcional** de control documental; no hay versiones
publicadas ni binarios distribuidos. Solo se presta soporte a la rama `main`.

| Rama | Soporte |
|---|---|
| `main` | Sí |
| Resto de ramas | No |

## Cómo reportar una vulnerabilidad

**No abras un issue público** con detalles de un fallo de seguridad.

1. Usa la opción *Report a vulnerability* del repositorio (GitHub Security
   Advisories) para abrir un aviso privado con los mantenedores.
2. Incluye la mayor información posible:
   - Descripción del fallo y su impacto.
   - Pasos de reproducción o prueba de concepto.
   - Versión de .NET, sistema operativo y commit afectado.
   - Cualquier mitigación que hayas identificado.
3. Se intentará acusar recibo en un plazo orientativo de 7 días hábiles y
   coordinar contigo la divulgación una vez corregido el fallo.

No se ofrece recompensa económica por los reportes.

## Alcance y advertencia importante

- **Esta aplicación es una demo y solo debe ejecutarse en `localhost`.**
- La autenticación por cookie ya está implementada (`/login`, RBAC en servidor y
  `Demo:LocalOnly`), pero las credenciales de demostración viven en
  `appsettings.json`. **No es un sistema de identidad de producción**: antes de
  publicarla hay que sustituirlas por ASP.NET Core Identity u OIDC, activar TLS y
  revisar la matriz de permisos.
- No la expongas a Internet ni a una red compartida, y no la publiques detrás de
  un proxy o túnel sin una capa de autenticación delante.
- No introduzcas datos reales ni sensibles: es un entorno de demostración.
- La cadena de conexión de ejemplo usa `Integrated Security` y
  `TrustServerCertificate=True` para el SQL Server local. No la reutilices en un
  despliegue remoto.
- Mantén actualizado el SDK de .NET dentro de la línea 10.0.x y revisa los
  avisos de dependencias (NuGet) de forma periódica.

## Fuera de alcance

- Vulnerabilidades en componentes de terceros no soportados por el proyecto
  (SQL Server, Ollama, etc.): repórtalas a sus responsables.
- Ataques que requieran acceso físico al equipo o credenciales del sistema
  operativo.
- Configuraciones deliberadamente inseguras documentadas como tales en la demo.
