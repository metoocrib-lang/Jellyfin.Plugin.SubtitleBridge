# ZimaBoard 2: agente controlado desde Jellyfin (experimental)

Tu Jellyfin 12.2, Sonarr, Radarr y Bazarr ya están conectados desde el plugin. Solo se agrega **un contenedor auxiliar de Chromium** para procesar subtítulos accesibles. La interfaz de series, idiomas, URLs y tarea diaria se queda en Jellyfin. Este agente no instala extensiones de Chrome ni necesita acceso al host Docker.

## 1. Preparar archivos y secretos localmente

En una terminal SSH de la ZimaBoard:

```sh
git clone https://github.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge.git
cd Jellyfin.Plugin.SubtitleBridge
cp extractor/.env.agent.example extractor/.env
openssl rand -hex 32
```

Copia el token aleatorio en `BRIDGE_API_TOKEN` dentro de `extractor/.env`. Elige otra contraseña aleatoria de 12+ caracteres para `VNC_PASSWORD` (NO tu clave de Disney+). Nunca subas ese archivo al repositorio.

## 2. Red privada Docker

Crea una única red compartida y conecta Jellyfin y Bazarr usando los nombres REALES de tus contenedores:

```sh
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}'
docker network create moonfin-subtitle-net
docker network connect moonfin-subtitle-net TU_CONTENEDOR_JELLYFIN
docker network connect moonfin-subtitle-net TU_CONTENEDOR_BAZARR
```

Si algún contenedor ya está conectado a la red, no repitas su conexión. El agente usará la misma red al arrancar. Mantén los puertos externos cerrados: el 8787 no se publica y el 6080 solo escucha en 127.0.0.1 de la ZimaBoard.

Inicia el agente desde el directorio del repositorio:

```sh
docker compose -f extractor/compose.agent.yml up -d --build
docker compose -f extractor/compose.agent.yml ps
```

Si usas otras redes o un proxy inverso, verifica que el contenedor del agente resuelva la URL de Bazarr configurada en Jellyfin. No necesitas Docker socket dentro de Jellyfin.

## 3. Configurar todo en Jellyfin

En **Moonfin Subtitle Bridge v0.6**:

1. En “Motor de streaming y cuentas”, URL `http://moonfin-streaming-agent:8787` y token igual que `BRIDGE_API_TOKEN`. Guarda.
2. Abre “Cargar series de Sonarr”, elige una serie y la fuente `Disney+`.
3. Especifica la **URL de la página de la serie** en Disney+ (también se admite `https://www.disneyplus.com/es-419/browse/entity-UUID`), idioma (por ejemplo `es`) y marca Automático. Si Sonarr y Disney+ organizan distinto las temporadas, añade equivalencias (`17x1-13=1x1` etc.) en la configuración de esa serie. Guarda.
4. En “Cuentas de streaming”, pulsa “Iniciar sesión en Disney+”.

## 4. Iniciar sesión en Disney+ sin compartir credenciales

Desde tu ordenador, abre un túnel SSH a la ZimaBoard (deja esa terminal abierta):

```sh
ssh -L 6080:127.0.0.1:6080 TU_USUARIO_SSH@IP_LAN_ZIMABOARD
```

Abre `http://localhost:6080/vnc.html` y utiliza **VNC_PASSWORD**, que solo es la contraseña del escritorio remoto. Accede a Disney+ en ese navegador, selecciona tu perfil y completa la autenticación normal durante la ventana de 4 minutos. No envíes contraseñas, cookies ni tokens al chat. Al terminar, consulta el estado en el plugin.

## 5. Ejecutar la revisión y comprobar resultados

Cuando Jellyfin registre un episodio nuevo, el plugin comprobará automáticamente su serie contra Sonarr mediante TVDB y verificará que Bazarr lo considera pendiente en el idioma elegido. Entonces encolará solo ese capítulo. También puedes pulsar “Revisar ahora” para procesar hasta 30 episodios pendientes de todas las series seleccionadas (recuperación manual). No hay revisión diaria. Consulta el botón “Consultar estado y trabajos” para ver códigos:

- `en_cola`, `procesando`: aún no se ha descargado nada.
- `enviado_a_bazarr`: Bazarr aceptó el archivo, comprueba la reproducción en Jellyfin.
- `episodio_no_identificado`: la página Disney+ no expuso un enlace S/E inequívoco; NO se importó un capítulo incierto.
- `pista_cifrada_no_admitida`: no se intentará descifrar.
- `sin_subtitulo_accesible_o_sesion`: revisar autenticación y disponibilidad de pistas.
- `conector_pendiente`: Netflix, Prime Video, Max y Apple TV+ todavía no tienen adaptador.

Para páginas que muestran episodios como botones sin enlaces de reproducción explícitos, el selector conservador aún puede responder `episodio_no_identificado`. Se admiten rótulos `T1:E1` y `S1:E1` cuando los enlaces directos están disponibles. No se identifica un episodio por posición en la página.

El agente usa una espera de 24 horas entre intentos sobre el mismo episodio/idioma y no ejecuta múltiples navegadores a la vez. Si la sesión caduca, vuelve a pulsar “Iniciar sesión” para renovarla.

**Estado de validación:** pruebas unitarias y de construcción Docker en GitHub; todavía requiere probar reproducción real de Disney+ en tu ZimaBoard. La GPU, DRM, el navegador y la web del proveedor pueden impedir acceso a las pistas. Nunca se eluden licencias o controles de acceso.
