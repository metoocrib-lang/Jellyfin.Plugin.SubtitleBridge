# Moonfin Subtitle Bridge — Jellyfin 12.2

Complemento nativo experimental para Jellyfin 12.2 (.NET 10) que permite seleccionar series y gestionar sus preferencias de subtítulos desde el Dashboard de Jellyfin. Diseñado para comunicarse con un agente local auxiliar que integra Bazarr, Sonarr y un navegador con sesión autenticada de Disney+.

**Estado actual: desarrollo experimental; aún no es una versión lista para instalar.**

## Arquitectura

- El plugin C# contiene la página de configuración y un controlador autenticado para administradores.
- Un agente separado gestiona preferencias, importación de subtítulos a Bazarr y sesiones de navegador. No guarda credenciales de Disney+ en Jellyfin.
- Moonfin utiliza la biblioteca de Jellyfin; el plugin no modifica el cliente Moonfin.
- Los subtítulos protegidos, DRM y controles de acceso no se eluden.

## Desarrollo

Jellyfin 12.2 requiere .NET 10. Ejecutar:

```bash
dotnet build Jellyfin.Plugin.SubtitleBridge/Jellyfin.Plugin.SubtitleBridge.csproj -c Release
```

Los workflows de GitHub Actions compilan y ejecutan comprobaciones. **No etiquetar ni publicar releases hasta confirmar que compilan y cargan correctamente.**

## Despliegue

El repo comienza privado. Un catálogo remoto en Jellyfin necesita URLs públicas (o un sistema privado de distribución controlado); para utilizar un repositorio GitHub estándar, publicar el repositorio únicamente cuando autorices visibilidad pública y haya una release válida.

La red local Docker no es accesible desde GitHub Actions; la instalación y prueba contra el servidor Jellyfin local requieren una acción autorizada desde tu equipo. No subas archivos `.env`, tokens API ni perfiles/cookies de navegadores.

Proyecto independiente no afiliado a Jellyfin, Moonfin, Disney+ o Bazarr.