# Moonfin Subtitle Bridge — Jellyfin 12.2

Plugin nativo para administrar series, idiomas y fuentes de subtítulos desde el Dashboard de Jellyfin.

## Instalación

**¿Usas un NAS distinto de ZimaBoard?** Consulta la [guía de instalación en Synology, QNAP, TrueNAS SCALE, Unraid y NAS Linux con Docker](docs/NAS_INSTALLATION.md). Contiene requisitos, pasos de instalación por terminal, diferencias entre plataformas, red privada y seguridad del agente.

**Repositorio Jellyfin** (Dashboard → Plugins → Repositories → Add):
```
https://raw.githubusercontent.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/main/manifest.json
```

En **Plugins → Catalog**, instala `Moonfin Subtitle Bridge` y reinicia Jellyfin. Versión actual: 0.9.1.0 (Jellyfin 12.2, .NET 10).

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

**Límites importantes:** la búsqueda manual de Bazarr requiere pulsar Buscar para el episodio concreto y depende de los idiomas faltantes según los perfiles de Bazarr. El seguimiento de streaming automático **sí existe mediante eventos de Jellyfin para episodios nuevos**; no hay una revisión periódica diaria. Las equivalencias por serie permiten adaptar la numeración de Sonarr a Disney+; la extracción sigue siendo experimental y requiere el agente Docker con sesión autorizada. Ni el plugin ni el agente evaden DRM.

## Desarrollo y pruebas

```sh
dotnet build Jellyfin.Plugin.SubtitleBridge/Jellyfin.Plugin.SubtitleBridge.csproj -c Release
```

GitHub Actions compila, genera ZIP y ejecuta un smoke test con Jellyfin 12.2 Docker. No afirma probar tu red Docker, la página con claves reales o la extracción de Disney+.

Nunca publiques API keys, contraseñas, cookies ni perfiles de navegador en GitHub. El plugin almacena las API keys en la configuración del servidor Jellyfin (protege tu directorio /config, respaldos y usuarios administradores). Proyecto independiente y no afiliado a Jellyfin ni plataformas.


## Extractores desde plataformas de streaming (experimental)

El extractor Disney+ está en [`extractor/`](extractor/README.md): navegador local con sesión iniciada por el usuario, captura HLS/WebVTT accesible, conversión a SRT e importación opcional en Bazarr. Actualmente incluye un **agente residente** que recibe episodios desde Jellyfin y un modo de prueba manual por URL de episodio. La identificación del episodio en el catálogo sigue siendo conservadora y puede fallar si Disney+ cambia su web. Netflix, Prime, Max y Apple TV+ son conectores pendientes. No se eluden DRM ni controles de acceso.

## Centro de streaming y activación al detectar un episodio nuevo (experimental)

El plugin permite elegir **plataforma + idioma + URL de serie + Automático** para cada serie de Sonarr. Reacciona a `ItemAdded` de Jellyfin para episodios nuevos (con identificación TVDB coincidente con Sonarr), **sin revisión periódica**. Desde v0.9.1, los episodios detectados automáticamente se validan por consulta exacta de serie y episodio en Bazarr. El botón **Revisar ahora** sigue siendo una revisión manual global de `/api/episodes/wanted` y puede mandar hasta 30 episodios pendientes al agente Docker autenticado.

**El agente está aislado para que Chromium no se ejecute dentro de Jellyfin.** Se configura desde el plugin con la URL `http://moonfin-streaming-agent:8787` y el mismo token privado `BRIDGE_API_TOKEN` del contenedor. Las claves de Bazarr se transmiten al agente solo en cada trabajo, por la red privada Docker; nunca se escriben en sus archivos de estado. Protege igualmente la red y las copias del directorio de configuración de Jellyfin.

Para las cuentas de streaming, pulsa “Iniciar sesión en Disney+” y autentícate directamente en el navegador mediante noVNC/túnel SSH; **no introduzcas contraseñas en el plugin**. El plugin mostrará el estado declarado por el agente (perfil guardado NO significa autenticación verificada).

**Límite actual:** Solo Disney+ cuenta con un extractor experimental. El agente intenta identificar el episodio desde enlaces de reproducción claramente marcados S01E02 o equivalentes en la página de la serie; si la web no muestra esos datos, deja constancia del episodio sin identificar. No se garantiza extracción en streaming comercial ni soporte de DRM. Netflix, Prime Video, Max y Apple TV+ aparecen como conectores pendientes sin falsa promesa de descarga.

Guías de instalación: [otros NAS — Synology, QNAP, TrueNAS SCALE, Unraid y Linux Docker](docs/NAS_INSTALLATION.md) y [ZimaBoard 2](extractor/ZIMABOARD.md).

### Equivalencias entre numeraciones (v0.7, experimental)

Para series como **Bleach: Thousand-Year Blood War**, Sonarr/Jellyfin pueden usar una sola temporada 17 mientras Disney+ muestra cuatro temporadas separadas. El campo **Equivalencias Sonarr → streaming** de la serie admite una regla por línea:

```text
17x1-13=1x1
17x14-26=2x1
17x27-40=3x1
17x41-50=4x1
```

Ejemplo: `17x49=4x9`. Las reglas son por serie, pueden representar rangos o episodios individuales, no admiten superposiciones ni un destino compartido. **Campo vacío**: numeración idéntica. **Campo con reglas**: los episodios sin equivalencia se omiten para evitar asignaciones erróneas. Los ID de serie y episodio de Sonarr/Bazarr no cambian: solo se traducen temporada y episodio enviados al agente para localizar la reproducción. Las equivalencias se aplican tanto a revisiones manuales como a episodios nuevos. Verifica los límites de temporadas cuando el catálogo cambie.

### Buscador de series y películas (v0.8)

En **Biblioteca y preferencias**, después de pulsar **Cargar series de Sonarr** o **Cargar películas de Radarr**, escribe parte del título en el campo **Buscar en series o películas cargadas**. El filtro funciona inmediatamente en la lista ya descargada: no realiza peticiones a Sonarr/Radarr por cada letra, ignora mayúsculas y acentos, también permite filtrar por ID y muestra cuántos resultados hay. Cambiar entre series y películas limpia el filtro para evitar ocultar resultados por accidente. Filtrar no modifica las configuraciones de seguimiento.

### Procesar un episodio (manual, v0.8.1)

En **Moonfin Subtitle Bridge → Motor de streaming y cuentas → Procesar un episodio**, elige una serie de Sonarr guardada con fuente Disney+, escribe la **temporada y episodio según Sonarr** y confirma explícitamente el envío. Por ejemplo, `Bleach` T17E02 se traduce a Disney+ T1E02 usando las reglas de equivalencias. El botón funciona aunque **Automático esté desactivado** y no inicia una revisión masiva.

El plugin consulta en Bazarr solo la serie elegida, verifica ID, temporada/episodio, archivo y subtítulo de idioma pendiente, rechaza registros ambiguos y envía como máximo **un trabajo** al agente. Si Bazarr ya tiene el subtítulo, el agente rechazó el trabajo o sigue en período de espera de 24 horas, se informa de ello; un trabajo aceptado en cola **no demuestra importación exitosa**. Pulsa **Consultar estado y trabajos** y verifica Bazarr/Jellyfin después de finalizar.

**Errores de procesamiento individual (v0.8.1):** el botón informa ahora de la causa segura y un código corto como `unmapped_episode`, `bazarr_unauthorized`, `agent_unreachable` o `agent_rejected` en lugar de responder siempre con un genérico HTTP 409. El código de error identifica dónde revisar; nunca muestra tokens, respuestas completas o URLs firmadas. Si aparece un mensaje, no vuelvas a enviar el trabajo sin revisarlo.

### Probar detección automática (solo lectura, v0.9)

En **Motor de streaming y cuentas**, selecciona la serie Sonarr (por ejemplo Bleach), escribe temporada y episodio según Sonarr (17 y 3) y pulsa **Probar detección automática**. Esta comprobación de diagnóstico **no importa subtítulos, no envía solicitudes al agente, no altera archivos y no simula el evento ItemAdded**. Muestra paso a paso si la serie tiene Automático habilitado, el agente está configurado, Sonarr devuelve TVDB, el episodio físico ya existe en Jellyfin con TVDB coincidente, hay una regla de equivalencia, y el episodio figura pendiente en el listado de Bazarr (consulta completa de la serie en Bazarr). Si alguna comprobación falla, revísala antes de realizar pruebas con episodios nuevos. La versión 0.9.1 usa la misma consulta de episodio específico que el botón manual, porque la lista global de pendientes de Bazarr puede omitir episodios descargados. Si procesaste T17E03 manualmente y ya tiene subtítulos, es correcto que el diagnóstico deje de marcarlo como pendiente.

El resultado «requisitos listos» no garantiza que se produzca una detección automática retroactiva: solo los episodios realmente nuevos generan ItemAdded en Jellyfin. Para episodios ya indexados usa el botón manual separado **Procesar un episodio** si necesitas importar subtítulos; esa acción sí modifica Bazarr tras confirmación.

**Corrección de detección automática en 0.9.1:** los eventos de episodio nuevo, que ya conocen serie/temporada/episodio, consultan `/api/episodes?seriesid[]=ID` en lugar de depender de las primeras 500 filas de `/api/episodes/wanted`. El episodio debe tener archivo físico y necesitar el idioma configurado; su ID Sonarr no cambia y la traducción de T/E a Disney+ se conserva. La consulta global para Revisar ahora (manual) mantiene su límite de 30 trabajos. El diagnóstico sigue siendo de solo lectura.

### Activación basada en eventos

La versión 0.6.0.0 utiliza `ILibraryManager.ItemAdded` y `ItemUpdated` (solo para episodios observados primero en ItemAdded). Necesita TVDB coincidente entre Jellyfin y Sonarr. En caso contrario **no realiza una asociación especulativa**; revisa los metadatos de ambas series. Se esperan 20 segundos para la indexación del medio antes de consultar Bazarr. No existe una tarea diaria, y los episodios importados mientras Jellyfin estuvo desconectado se pueden cubrir usando el botón manual **Revisar ahora**.
