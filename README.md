# Moonfin Subtitle Bridge — Jellyfin 12.2

Plugin nativo para administrar series, idiomas y fuentes de subtítulos desde el Dashboard de Jellyfin.

## Instalación

**Repositorio Jellyfin** (Dashboard → Plugins → Repositories → Add):
```
https://raw.githubusercontent.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/main/manifest.json
```

En **Plugins → Catalog**, instala `Moonfin Subtitle Bridge` y reinicia Jellyfin. Versión actual: 0.5.0.0 (Jellyfin 12.2, .NET 10).

## Configurar URLs de Bazarr / Sonarr / Radarr

En los ajustes del plugin coloca las direcciones accesibles **desde el contenedor Jellyfin** y las claves API de cada servicio; por ejemplo, si todos comparten una red Docker:

| Servicio | URL ilustrativa | ¿Dónde está la clave API? |
|---|---|---|
| Bazarr | `http://bazarr:6767` | Settings → General → Security |
| Sonarr | `http://sonarr:8989` | Settings → General → Security |
| Radarr | `http://radarr:7878` | Settings → General → Security |

Son ejemplos: usa los nombres Docker reales, IP de LAN o URL del proxy con ruta base si corresponde. Pulsa **Guardar y probar** en cada servicio: primero guarda la URL y la clave introducidas y después prueba la conexión. También puedes usar **Guardar todas las conexiones**. Las claves no se muestran en la pantalla al volver a abrirla; dejar el campo vacío preserva la clave guardada.

El servidor Jellyfin llama a las APIs de los servicios directamente, con `X-Api-Key` (no desde JavaScript ni el navegador). Los endpoints del plugin exigen sesión de administrador. Los servicios pueden usarse sin instalar el agente Docker.

- **Probar Bazarr/Sonarr/Radarr**: conexión autenticada y versión de servicio (si la API la devuelve).
- **Cargar series/películas**: obtiene listas de Sonarr y Radarr sin agente.
- **Seguir título**: guarda en la configuración del plugin Jellyfin el idioma, fuente y opción de seguimiento automático.
- **Ver episodios pendientes en Bazarr**: para una serie seguida con fuente Proveedores Bazarr, consulta los capítulos que Bazarr marca como faltantes en el idioma guardado, sin descargarlos.
- **Buscar subtítulo en Bazarr**: solicita manualmente la búsqueda para **un episodio** mediante PATCH /api/episodes/subtitles. No garantiza resultados ni finalización inmediata.

**Limitación importante:** la búsqueda en Bazarr es por episodio y requiere pulsar Buscar. El seguimiento automático se guarda, pero todavía no existe un planificador. El listado depende de los idiomas faltantes según los perfiles de Bazarr (un episodio sin perfil deseado no aparecerá). Falta implementar el planificador y la vinculación de episodios de Disney+. El agente de navegador es opcional y experimental, y sigue siendo necesario para intentar extraer subtítulos de Disney+ en una sesión autorizada. Ni el plugin ni la configuración directa evaden DRM.

## Desarrollo y pruebas

```sh
dotnet build Jellyfin.Plugin.SubtitleBridge/Jellyfin.Plugin.SubtitleBridge.csproj -c Release
```

GitHub Actions compila, genera ZIP y ejecuta un smoke test con Jellyfin 12.2 Docker. No afirma probar tu red Docker, la página con claves reales o la extracción de Disney+.

Nunca publiques API keys, contraseñas, cookies ni perfiles de navegador en GitHub. El plugin almacena las API keys en la configuración del servidor Jellyfin (protege tu directorio /config, respaldos y usuarios administradores). Proyecto independiente y no afiliado a Jellyfin ni plataformas.
