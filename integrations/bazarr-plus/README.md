# Moonfin Bridge para Bazarr+ (prototipo Provider Hub v1)

**Estado: experimental, no instalado ni publicado aún en el Marketplace de Bazarr+.** Este directorio forma parte del repositorio de Moonfin Subtitle Bridge. No sustituye Bazarr oficial ni cambia su configuración. Probado únicamente mediante pruebas sin red/credenciales y el pipeline CI de GitHub; todavía **no** se ha verificado un flujo real Bazarr+ → Disney+ en el NAS.

## Qué hace

- `provider.py` implementa exactamente las operaciones `search(video, languages, config)` y `download(provider_payload, language, config)` del [Provider Hub v1](https://github.com/LavX/bazarr-provider-catalog/blob/main/sdk/README.md).
- El proveedor **solo anuncia subtítulos SRT ya descargados** en la caché privada del agente; nunca inventa un resultado mientras la extracción siga en la cola.
- Si el administrador marca `enable_requests=true` en Bazarr+, una búsqueda de un episodio de una serie configurada puede **poner en cola** una extracción en el agente. Esa primera búsqueda normalmente no ofrecerá un resultado: debe repetirse después, cuando se haya almacenado el SRT. **No es una búsqueda síncrona ni garantiza que Disney+ tenga una pista accesible.**
- `download()` solicita el SRT a la API privada del agente, comprueba el SHA-256 y devuelve `content_b64` al gestor de subtítulos. **Bazarr+ es quien importaría el SRT** si elige ese resultado; el proveedor no hace llamadas a la API de carga de Bazarr.
- Solo se soportan **episodios de Disney+ con subtítulos españoles normales (`spa`, no HI/forced)**, identificados por ID de serie TVDB. No se soportan películas ni otros servicios. La sesión de Disney+ se mantiene en el contenedor del agente.
- La integración antigua con Bazarr oficial **se mantiene intacta y funciona como antes**. El agente distingue `delivery=bazarr_plus` para no cargar un trabajo del proveedor usando identificadores ficticios.

## Estructura

```text
integrations/bazarr-plus/
  provider.py       # Provider Hub v1, stdlib, sin dependencias externas
  provider.json     # Esquema de ajustes y hashes para futura publicación
  README.md         # Esta guía
extractor/
  provider_bridge.py  # Caché de SRT; persistida en streaming_state
  server.py           # /v1/provider/lookup y /v1/provider/download
```

## Requisitos para una futura prueba aislada

1. Conservar tu **Bazarr oficial actual sin cambios**. Levantar **Bazarr+ en un contenedor de prueba diferente**, con su propio `/config` y preferiblemente sin escritura sobre los subtítulos de tu biblioteca real. No poner dos gestores a modificar los mismos SRT.
2. Reconstruir **únicamente el contenedor del agente de prueba** (el código Python nuevo debe estar dentro de su imagen). La nueva API no requiere actualizar el plugin .NET.
3. Conectar **el contenedor/worker de Bazarr+** y el agente a la misma red Docker privada `moonfin-subtitle-net`. Probar conectividad desde el lugar donde se ejecuta el worker, no desde el navegador de tu PC. No publicar `8787` ni noVNC.
4. Autenticar Disney+ dentro del perfil persistente del agente mediante noVNC y un túnel SSH, como explica [la guía multi-NAS](../../docs/NAS_INSTALLATION.md).

**Aviso:** el catálogo comunitario Bazarr+ **todavía no contiene Moonfin**. Colocar `provider.py` y `provider.json` aquí **no** hace que aparezca automáticamente en el Marketplace. Primero hay que validar con el worker real de Bazarr+, adaptar el manifiesto a la ubicación del catálogo, regenerar hashes y enviar una PR al [catálogo de Bazarr+](https://github.com/LavX/bazarr-provider-catalog). No cambiar Bazarr oficial antes de esa prueba.

## Ajustes previstos en el Provider Hub

| Campo | Ejemplo | Descripción |
| --- | --- | --- |
| `agent_url` | `http://moonfin-streaming-agent:8787` | Dirección privada del agente desde Bazarr+, no la URL de Jellyfin |
| `bridge_token` | **token secreto** | El mismo valor que `BRIDGE_API_TOKEN` del agente, al menos 32 caracteres |
| `series_json` | Ver abajo | Lista explícita de TVDB → URL Disney+ y equivalencias; máximo 32 series |
| `enable_requests` | **false** | Activación explícita de la cola de extracción cuando Bazarr+ busca |

Ejemplo de configuración de **una serie** para Bleach, sustituyendo **123456** por el valor **TVDB real**, que se puede consultar en Sonarr. **No confundas TVDB con el ID interno de Sonarr** (por ejemplo, Sonarr 42 no es necesariamente TVDB 42).

```json
[
  {
    "tvdb_id": 123456,
    "series_url": "https://www.disneyplus.com/es-419/browse/entity-571475d5-8b1a-453f-a57f-26bb4dd13304",
    "mapping": "17x1-13=1x1\n17x14-26=2x1\n17x27-40=3x1\n17x41-50=4x1"
  }
]
```

Este es un **primer adaptador independiente**: por ahora duplica las equivalencias de Jellyfin en `series_json`. Una versión futura podría sincronizarlas a través de un contrato de API limitado sin compartir claves de Bazarr.

Las reglas vacías implican numeración idéntica. Una lista no vacía es **estricta**: capítulos sin regla no se encolan. El proveedor rechaza intervalos solapados o destinos duplicados; únicamente acepta URLs de páginas de serie Disney+ HTTPS en dominios autorizados.

## Flujo de un episodio

```text
Bazarr+ busca: vídeo TVDB 123456, Sonarr T17E03, idioma spa
  ├── la serie no está en series_json / idioma no es spa → []
  └── POST agente /v1/provider/lookup (X-Bridge-Key)
        ├── SRT ya en caché → "ready" + SHA-256
        │     ├── search() ofrece el resultado
        │     └── Bazarr+ selecciona el resultado
        │           → download() → POST /v1/provider/download
        │           → bytes SRT verificados → Bazarr+ importa
        ├── sin caché y enable_requests=false → "absent" → []
        └── sin caché y enable_requests=true → "queued"/"pending"/"busy" → []
              └── agente busca Disney+ T1E03 (si la sesión permite)
                    └── guarda SRT atómicamente en volumen streaming_state
                          └── siguiente búsqueda Bazarr+ puede ofrecerlo
```

El agente impone una espera de hasta 24 horas para reintentar un episodio fallido y nunca inicia navegadores simultáneos. `streaming_state` contiene copias locales de subtítulos (además del historial); protégelo, respáldalo con cautela y revisa permisos. La **caché no se purga automáticamente** en este primer prototipo.

## Validación para desarrolladores

Desde la raíz del repositorio:

```sh
python3 -B -m unittest extractor.tests.test_bazarr_plus_provider -v
python3 -B -m unittest discover -s extractor/tests -v
```

Las pruebas no contactan Disney+, Sonarr ni Bazarr; simulan la búsqueda, la cola y la descarga con un SRT sintético y verifican que un episodio/serie/idioma distinto no se ofrece ni descarga.

En una fase posterior, clonar `LavX/bazarr-provider-catalog` y copiar `provider.py`/`provider.json` a `providers/moonfinbridge/` para las pruebas del SDK:

```sh
python3 -B -m sdk build-catalog
python3 -B -m sdk validate
python3 -B -m sdk smoke-test --provider moonfinbridge ...
```

Estas instrucciones del SDK dependen de las interfaces vigentes de Bazarr+; **no** deben ejecutarse contra la biblioteca real ni con búsquedas automáticas activas sin una configuración de prueba. El manifiesto local usa procedencia `trusted=false` y una referencia de commit provisional; antes de una publicación oficial hay que actualizar la procedencia a una revisión inmutable y generar de nuevo el catálogo/hashes conforme a la guía del proyecto.

## Riesgos y seguridad

- El **token** otorga acceso a la API privada, incluyendo extracciones y descargas. Configúralo como secreto de Provider Hub; no lo escribas en `series_json`, `provider_payload`, logs, capturas ni GitHub.
- **No abras `8787` a la red pública.** Tanto la URL del agente como el perfil del navegador deben mantenerse privados.
- La búsqueda con `enable_requests=true` puede generar trabajo aunque Bazarr+ termine seleccionando otro proveedor; por eso comienza con `false` y activa primero para **una serie y un episodio de prueba**.
- Moonfin no descifra DRM ni garantiza compatibilidad Disney+ / Chromium, idiomas regionales, tiempos de extracción o éxito de importación.
- Este prototipo **no** cambia el comportamiento de `/v1/scan` ni de **Procesar un episodio** de Jellyfin; no hay migración forzada de Bazarr.
