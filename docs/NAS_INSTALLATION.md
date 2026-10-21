# Instalar Moonfin Subtitle Bridge en otros sistemas NAS

Esta guía cubre **Synology DSM, QNAP QTS/QuTS hero, TrueNAS SCALE, Unraid y Linux con Docker**. Se basa en los archivos reales de este repositorio y en Moonfin **0.9.1.0 para Jellyfin 12.2 (.NET 10)**. Si el catálogo publica otra versión, comprueba la compatibilidad ABI en `manifest.json`. La instalación en ZimaBoard 2 está explicada por separado en [ZIMABOARD.md](../extractor/ZIMABOARD.md).

> **Alcance de las pruebas:** el plugin se compila y se inicia con Jellyfin 12.2 en GitHub Actions; su funcionamiento con Bazarr, Jellyfin y Disney+ se ha comprobado en una instalación ZimaBoard. **No se ha verificado físicamente en cada marca/modelo NAS**. Las indicaciones de interfaces de fabricantes proceden de sus manuales, no de instalaciones Moonfin probadas allí.

## 1. Componentes y requisitos

| Componente | Dónde se instala | Función |
| --- | --- | --- |
| Jellyfin **12.2** | NAS o servidor compatible | Ejecuta el plugin .NET 10; las versiones antiguas de Jellyfin no están validadas |
| Moonfin Subtitle Bridge | Catálogo de plugins de Jellyfin | Configura series, preferencias, disparadores y pruebas |
| Sonarr y Bazarr | Contenedores/servicios alcanzables desde Jellyfin | Identifican series/episodios y gestionan subtítulos |
| Agente `moonfin-streaming-agent` | Contenedor Docker aparte, recomendable en el **mismo host Docker** que Jellyfin | Chromium/Playwright, cola privada y navegador noVNC |
| Radarr | Opcional | Catálogo de películas; el extractor automatizado de películas no está implementado |

- Necesitas **Docker Engine + Docker Compose v2**, posibilidad de construir imágenes desde `extractor/Dockerfile`, conexión saliente para obtener la imagen base/paquetes y recursos suficientes para un navegador Chromium. Comprueba el NAS con `docker version`, `docker compose version` y `uname -m`. Los nombres de menús y la disponibilidad de Docker varían según modelo, firmware y arquitectura.
- La imagen base actual es `mcr.microsoft.com/playwright/python:v1.56.0-noble` (Ubuntu 24.04) con Playwright Python 1.56.0, más Xvfb, x11vnc, openbox y noVNC. **No hay una imagen del agente publicada en el registro de este proyecto**: el Compose construye `moonfin-streaming-agent:dev` localmente.
- **CPU:** `x86_64`/`amd64` es la configuración de referencia. Microsoft publica variantes Playwright `arm64`, pero **este agente no está probado en NAS ARM64**; verifica compatibilidad de todas las capas y navegadores, y que `docker compose ... build` funcione. ARM de **32 bits no se considera compatible** con esta receta. La disponibilidad de Docker tampoco está garantizada en todos los modelos Synology/QNAP.
- El contenedor reserva **1 GiB de memoria compartida (`shm_size`)** para Chromium; no equivale a garantizar que 1 GiB de RAM total sea suficiente. El rendimiento y los requisitos de memoria dependen del NAS.
- Se necesita una cuenta legítima del proveedor de streaming. El agente **no evade DRM**, no garantiza reproducción de Disney+ en Linux/Chromium y solo puede procesar pistas HLS/VTT accesibles. Netflix, Prime Video, Max y Apple TV+ **no disponen de extractores funcionales**.

## 2. Instalación común por SSH o terminal del NAS

Este es el **camino recomendado** cuando Docker y Compose funcionan desde la terminal del NAS. Los pasos de interfaz gráfica específicos se indican más abajo. Los comandos se ejecutan en el **host NAS**, no dentro de un contenedor.

### A. Obtener el repositorio y guardar secretos

```sh
git clone https://github.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge.git
cd Jellyfin.Plugin.SubtitleBridge
umask 077
cp extractor/.env.agent.example extractor/.env
openssl rand -hex 32
```

Edita `extractor/.env` con un editor local (`nano`, `vi` o la administración segura de archivos del NAS):

- `BRIDGE_API_TOKEN`: copia el resultado del comando `openssl` (**64 caracteres hexadecimales**; el agente exige al menos 32).
- `VNC_PASSWORD`: contraseña **diferente**, de **12 caracteres o más**, para el escritorio remoto; **no es tu contraseña de Disney+**.
- Conserva `BROWSER_PROFILE=/data/browser` y `STATE_FILE=/data/state/attempts.json`.

No uses `extractor/.env.example` para el agente residente: ese archivo corresponde a la **prueba manual de un solo episodio**. El agente usa `extractor/.env.agent.example`.

**No subas `extractor/.env` a GitHub ni compartas las claves en capturas o registros.** Para instalar el plugin no es necesario proporcionar la contraseña de Disney+ a Moonfin.

### B. Crear la red privada de contenedores

```sh
docker ps --format 'table {{.Names}}\t{{.Status}}'
docker network create moonfin-subtitle-net
docker network connect moonfin-subtitle-net NOMBRE_CONTENEDOR_JELLYFIN
docker network connect moonfin-subtitle-net NOMBRE_CONTENEDOR_BAZARR
```

Sustituye los nombres. Si la red o las conexiones **ya existen**, no vuelvas a crearlas. Si Jellyfin ya puede consultar Sonarr mediante una URL/IP, no es obligatorio agregar Sonarr a esta red; si quieres usar `http://sonarr:8989`, también debe poder resolver ese nombre desde Jellyfin.

**Importante:** una red Docker creada con `docker network create` solo conecta contenedores del **mismo Docker Engine**. No conecta automáticamente dos NAS distintos. Si Jellyfin y el agente están en hosts diferentes, este Compose **no es una receta multi-host**: se necesita una red privada entre hosts, un endpoint del agente accesible solo por esa red y diseño adicional de seguridad. No abras `8787` a Internet para resolverlo.

Cuando Jellyfin/Bazarr se gestionen en proyectos Compose distintos, es preferible declarar la red externa `moonfin-subtitle-net` en **sus archivos Compose** para conservar la conexión después de recrearlos; una conexión manual `docker network connect` puede perderse al reemplazar el contenedor.

### C. Construir y arrancar el agente

Ejecuta estos comandos **desde la raíz del repositorio** (donde está `README.md`):

```sh
docker compose -f extractor/compose.agent.yml config --quiet
docker compose -f extractor/compose.agent.yml up -d --build
docker compose -f extractor/compose.agent.yml ps
```

Los archivos ya definen:

- Imagen compilada localmente `moonfin-streaming-agent:dev` y nombre del contenedor `moonfin-streaming-agent`.
- API del agente en **8787 solo dentro de Docker** (no publica ese puerto en el host).
- noVNC en **`127.0.0.1:6080` del NAS**, no en todas sus interfaces.
- Dos volúmenes Docker persistentes: `streaming_profile` (sesión de navegador) y `streaming_state` (registro de reintentos), cuyos nombres físicos pueden recibir el prefijo del proyecto Compose.
- Red externa `moonfin-subtitle-net`, `init: true`, `no-new-privileges` y `shm_size: 1gb`.

Para consultar el estado o diagnosticar arranque:

```sh
docker compose -f extractor/compose.agent.yml ps
docker compose -f extractor/compose.agent.yml logs --tail=60 streaming-agent
docker network inspect moonfin-subtitle-net
```

No publiques logs completos sin revisar secretos o información privada. **No borres los volúmenes** si deseas conservar la sesión y el historial. Al recrear un servicio desde una UI del NAS, comprueba que usa los mismos volúmenes.

### D. Instalar el plugin y configurar conexiones

1. En **Jellyfin 12.2 → Dashboard → Plugins → Repositories**, añade:
   `https://raw.githubusercontent.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/main/manifest.json`
2. En **Plugins → Catalog**, instala **Moonfin Subtitle Bridge**, reinicia Jellyfin y abre su configuración como administrador.
3. En las conexiones de Moonfin guarda y prueba las URL y API keys de Sonarr y Bazarr, **alcanzables desde el contenedor Jellyfin**. Ejemplos si comparten red:
   - `http://sonarr:8989`
   - `http://bazarr:6767`
4. En **Motor de streaming y cuentas**, configura la URL del agente `http://moonfin-streaming-agent:8787` y copia **el mismo** `BRIDGE_API_TOKEN`. Guarda y usa **Probar agente**.
5. Elige una serie de Sonarr, fuente **Disney+**, idioma (por ejemplo `es`), **URL de página de serie** y opcionalmente **Automático**; pulsa **Actualizar preferencias**. Si las temporadas no coinciden entre Sonarr y Disney+, utiliza reglas como `17x1-13=1x1`.

**Dos rutas distintas:** el botón **Probar detección automática** solo consulta Jellyfin/Sonarr/Bazarr; no importa subtítulos. **Procesar un episodio** solicita, con confirmación, un solo trabajo. Un episodio que ya existía en Jellyfin antes de habilitar el plugin no vuelve a generar el evento `ItemAdded`; las ejecuciones automáticas se disparan al detectar archivos nuevos.

### E. Abrir noVNC mediante túnel SSH

Desde tu PC (no desde el contenedor), mantén abierta esta conexión:

```sh
ssh -L 6080:127.0.0.1:6080 USUARIO_SSH@IP_LAN_DEL_NAS
```

Abre **http://localhost:6080/vnc.html** en el navegador de tu PC. Introduce `VNC_PASSWORD` y, cuando pulses **Iniciar sesión en Disney+** desde Moonfin, autentícate **directamente en el navegador remoto** durante la ventana disponible. No copies cookies ni tokens a Jellyfin o GitHub. NoVNC usa HTTP dentro del túnel; **no publiques el puerto 6080 en el router ni cambies el mapeo a `0.0.0.0`**.

Si el PC ya utiliza el puerto 6080, escoge otro **puerto local** en el túnel, por ejemplo `ssh -L 16080:127.0.0.1:6080 ...`, y visita `http://localhost:16080/vnc.html`.

## 3. Particularidades según NAS

### Synology DSM 7.2 o posterior — Container Manager

- Comprueba que **Container Manager está disponible en tu modelo**. En **Container Manager → Proyecto → Crear**, la interfaz permite seleccionar carpeta de proyecto y un archivo Docker Compose o editar YAML.
- **Recomendado:** habilita SSH de forma temporal y utiliza los comandos del apartado 2 con el repositorio completo. Esto conserva la relación entre `extractor/compose.agent.yml`, `build.context: ..`, `extractor/Dockerfile` y el archivo `extractor/.env`.
- Para gestionarlo por **Proyecto**, usa una carpeta del NAS que contenga **todo el repositorio**. Algunas instalaciones/UI no permiten construir una imagen desde un contexto externo a la carpeta del proyecto o referenciar `.env` del mismo modo; si ocurre, **construye primero la imagen por SSH** y adapta el Compose para usar solo `image: moonfin-streaming-agent:dev`, con una ubicación segura para variables y volúmenes.
- Confirma nombres de contenedores y redes: Synology puede renombrar las instancias según el proyecto. No asumas que se llaman simplemente `jellyfin` y `bazarr`.
- No confundas el acceso a DSM por HTTPS con el puerto local noVNC: usa el túnel SSH.

Referencia oficial: [Synology Container Manager — Proyecto](https://kb.synology.com/index.php/es-mx/DSM/help/ContainerManager/docker_project?version=7).

### QNAP QTS / QuTS hero — Container Station 3

- Confirma que tu modelo ofrece **Container Station** y motor Docker compatible. En **Container Station → Applications → Create** puedes administrar una aplicación Docker Compose mediante YAML.
- Como el Compose de Moonfin **construye una imagen local**, prefiere ejecutar la instalación por **SSH** con el repositorio completo o **construir la imagen primero** antes de importar un YAML adaptado en Container Station. Pegar solo el Compose en la UI **no aporta automáticamente el Dockerfile ni el `extractor/.env`**.
- Comprueba que el servicio Docker de QNAP utiliza la **misma red** donde están Jellyfin y Bazarr y que puede resolver sus nombres. NoVNC debe quedar enlazado únicamente a `127.0.0.1` del host NAS.
- Protege las carpetas de configuración y cualquier exportación de aplicaciones que contenga valores del entorno.

Referencia oficial: [QNAP — How to use Container Station 3](https://www.qnap.com/en-us/how-to/tutorial/article/how-to-use-container-station-3).

### TrueNAS SCALE — Apps con Docker, 24.10 o posterior

- **SCALE 24.10+** emplea un sistema de Apps basado en Docker y expone **Apps → Discover → menú → Install via YAML**, que acepta sintaxis Compose. En versiones más nuevas, los nombres concretos pueden cambiar.
- **SCALE 24.04 y anteriores usan el sistema anterior de Kubernetes:** **no** pegues ahí `compose.agent.yml` esperando compatibilidad Docker. Usa una versión con Apps Docker o un host Docker Linux separado.
- El archivo de este repositorio utiliza `build:`, `env_file:` y una **red externa preexistente**. No presupongas que el editor YAML de TrueNAS construye la imagen ni que comparte las redes de otras aplicaciones administradas. Valida soporte/limitaciones de tu versión; cuando no sea compatible, **construye la imagen en un Docker compatible** y utiliza una imagen disponible para la aplicación, configurando almacenamiento persistente y red privada de forma explícita.
- Los datasets tienen permisos propios. El perfil del navegador contiene cookies: utiliza almacenamiento persistente protegido, no un directorio público SMB/NFS. Los volúmenes nombrados del Compose y los datasets enlazados manualmente **no son equivalentes**: verifica la persistencia antes de actualizar.

Referencias oficiales: [TrueNAS — Apps Docker y YAML](https://apps.truenas.com/managing-apps/installing-custom-apps/) y [documentación 24.04 (Kubernetes)](https://www.truenas.com/docs/scale/24.04/scaletutorials/apps/usingcustomapp/).

### Unraid — Docker con herramienta Compose adicional

- Unraid **no incluye Docker Compose de forma nativa**. Puedes instalar y usar un gestor comunitario de stacks, como **Compose Manager Plus**, o instalar un Compose CLI compatible. Son herramientas **de terceros**, no un componente del propio Moonfin.
- Usa un directorio **persistente** para el repositorio/proyecto (no dependas de rutas temporales) y comprueba que `docker compose version` funciona antes de seguir el procedimiento general. Los volúmenes nombrados mantienen el navegador y el historial; incluirlos en tus copias de seguridad requiere decisiones de privacidad adicionales.
- Si Jellyfin/Bazarr se gestionan desde plantillas individuales en la pestaña **Docker**, añade la red compartida en sus opciones de red cuando sea posible y verifica las conexiones tras recrear un contenedor.
- El contenedor auxiliar se ejecuta aparte de Jellyfin, sin montar el socket Docker en Jellyfin y sin habilitar modo privilegiado.

Referencias: [Unraid — Docker overview](https://docs.unraid.net/unraid-os/using-unraid-to/run-docker-containers/overview/) y [Compose Manager Plus (tercero)](https://forums.unraid.net/topic/197334-plugin-compose-manager-plus/).

### NAS Linux genérico, mini-PC o servidor con Docker

Los comandos del **apartado 2** suelen ser suficientes en sistemas Linux con Docker Engine y Compose v2. En instalaciones sin permisos de grupo Docker, ejecuta los comandos mediante el mecanismo administrativo del sistema (por ejemplo `sudo`), sin exponer `docker.sock` al plugin. Si Jellyfin se ejecuta **fuera de Docker**, no podrá resolver `moonfin-streaming-agent` por DNS de Docker: se requiere un diseño de red privada diferente, que no proporciona automáticamente `compose.agent.yml`.

## 4. Verificación segura y fallos habituales

1. **Conexiones:** pulsa **Guardar y probar** para Sonarr y Bazarr. Si el nombre `bazarr` no resuelve desde Jellyfin, usa una dirección alcanzable desde ese contenedor o conéctalo a la red adecuada. Si el agente no ve Bazarr, tampoco podrá importar el SRT.
2. **Agente:** pulsa **Probar agente**. Error 401: token distinto. Error de red: red/hostname/puerto incorrectos. La API **8787 no necesita publicarse en el host** cuando Jellyfin y el agente comparten red Docker.
3. **Navegador:** abre noVNC con **SSH**; si aparece pantalla en blanco, revisa los registros y `shm_size`. Una sesión guardada **no garantiza** acceso a pistas Disney+.
4. **Serie:** guarda la **URL completa de la página de serie** y el idioma; no basta con escribirlos si falta pulsar **Actualizar preferencias**. Si el agente devuelve `agent_rejected`, verifica URL y formato de trabajo antes de reintentar.
5. **Episodio:** **Probar detección automática** confirma TVDB entre Jellyfin/Sonarr, reglas y pendientes en Bazarr **sin enviar trabajos**. Para un capítulo ya indexado, **Procesar un episodio** es una acción manual separada.
6. **Resultado:** `en_cola` no significa importación. Busca `enviado_a_bazarr` en **Consultar estado y trabajos** y comprueba los subtítulos en Bazarr/Jellyfin. No confundas una pista cifrada o una sesión caducada con un fallo del NAS.
7. **Reintentos:** el agente impone una espera de **24 horas por episodio/idioma**; no reenvíes repetidamente ni pulses **Revisar ahora** para una prueba de un solo episodio. La revisión manual global puede encolar hasta **30 trabajos**.

## 5. Seguridad, mantenimiento y límites de compatibilidad

- **No publicar** ni reenviar `BRIDGE_API_TOKEN`, claves de Bazarr/Sonarr, `VNC_PASSWORD`, contraseñas del proveedor, cookies, archivos `.env`, perfiles de navegador o volúmenes respaldados sin cifrar.
- **No publicar los puertos 8787/6080 en Internet.** El Compose actual solo enlaza 6080 a loopback; protege también el acceso SSH y las redes Docker. Un puerto Docker privado no sustituye aislamiento de usuarios/administradores del NAS.
- No habilites `privileged: true` ni montes `/var/run/docker.sock` para resolver problemas del agente. Chromium, noVNC y sus dependencias aumentan la superficie de ataque: evita visitas a sitios no confiables.
- Las copias de seguridad deben conservar **configuración de Jellyfin** y, si quieres mantener sesiones, los **dos volúmenes** del agente. Una copia del perfil de navegador contiene información de autenticación sensible.
- Después de actualizar o recrear Jellyfin/Bazarr, comprueba la red compartida y prueba conexiones. Para reconstruir el agente sin borrar su estado: `docker compose -f extractor/compose.agent.yml up -d --build`.
- Lo soportado por el **código** no equivale a compatibilidad **probada** en todo NAS, arquitectura CPU, firmware, DRM o versión de Jellyfin.

Más información: [README principal](../README.md), [agente experimental](../extractor/README.md), [instalación en ZimaBoard](../extractor/ZIMABOARD.md) y [Docker de Playwright](https://playwright.dev/python/docs/docker).
