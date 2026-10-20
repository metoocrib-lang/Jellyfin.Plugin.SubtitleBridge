# Pruebas de Moonfin Subtitle Bridge en Jellyfin 12.2

**Versión:** 0.3.1.0 experimental. No es un extractor totalmente autónomo de Disney+; requiere URLs vinculadas por episodio y un navegador local autenticado. Aún no se ha validado con una sesión real.

## A. Instalar el plugin desde el catálogo

Requisitos: repo GitHub **público**, workflow `Build and publish...` terminado con éxito, release `v0.3.1.0` creada y `manifest.json` con `versions` no vacío.

1. En Jellyfin 12.2, Dashboard → Plugins → Repositories → Add.
2. Nombre: `Moonfin Subtitle Bridge`
3. URL: `https://raw.githubusercontent.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/main/manifest.json`
4. Guardar; Dashboard → Plugins → Catalog → Moonfin Subtitle Bridge → Install; reiniciar Jellyfin una vez.
5. En Dashboard → Plugins → My Plugins → Moonfin Subtitle Bridge → Settings, configurar agente local (sección B).

Si el catálogo está vacío, comprobar antes de repetir la instalación que la URL raw devuelve JSON con una versión 12.2.0.0 y que la release incluye ZIP. **No intentar instalar el código fuente ZIP**; utilizar el ZIP compilado que genera GitHub Actions.

## B. Agente Docker auxiliar

El plugin solo gestiona la interfaz. El agente Python / navegador se ejecuta como otro contenedor. En el prototipo v0.3 ya construido, extraer el código del paquete de fuentes del proyecto; la migración completa del agente a este repositorio sigue pendiente.

1. Completar un archivo `.env` privado tomando `.env.example` como plantilla. Configurar `JELLYFIN_URL`, `JELLYFIN_API_KEY`, `BAZARR_URL`, `BAZARR_API_KEY`, `APP_SECRET`, `BRIDGE_PASSWORD`, y `BRIDGE_API_TOKEN` aleatorio con mínimo 24 caracteres.
2. Ejecutar `docker compose up -d --build moonfin-subtitle-bridge`.
3. Identificar los nombres reales de los contenedores: `docker ps --format '{{.Names}}'`.
4. Si Jellyfin y el agente están en proyectos Compose diferentes, crear y compartir red:
   ```sh
   docker network create moonfin-bridge-net
   docker network connect moonfin-bridge-net CONTENEDOR_JELLYFIN
   docker network connect --alias moonfin-subtitle-bridge moonfin-bridge-net CONTENEDOR_BRIDGE
   ```
   Es normal que el `docker network create` indique que ya existe si la red está creada.
5. En la página del plugin, **URL de agente**: `http://moonfin-subtitle-bridge:8787`; **token**: mismo valor que `BRIDGE_API_TOKEN`; Guardar; Probar conexión.
6. Docker Disney+ opcional: `docker compose --profile disney up -d --build`. Para autenticar la cuenta, usar directamente el navegador del agente en la red local; no copiar cookies ni contraseñas a GitHub o ChatGPT.

No publicar puertos 8787 ni 6080 en Internet. noVNC no tiene autenticación en el prototipo: usar acceso exclusivamente local / túnel SSH.

## C. Diagnóstico seguro a compartir

En tu servidor:
```sh
docker ps --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}'
docker compose logs --tail=70 moonfin-subtitle-bridge
```

En Jellyfin: capturar la página del plugin y su estado `Probar conexión`; o compartir los mensajes de error tras eliminar API keys, tokens, cookies, enlaces a listas HLS/CDN y rutas con datos sensibles. **No hace falta publicar tu servidor en Internet.**

## D. Validación progresiva

- [ ] GitHub Actions: compila plugin .NET 10.
- [ ] GitHub Release y manifiesto públicos y descargables.
- [ ] Jellyfin 12.2 arranca y muestra Moonfin Subtitle Bridge en plugins instalados.
- [ ] Página de configuración permite guardar URL y token.
- [ ] `Probar conexión` devuelve `ok`.
- [ ] `Actualizar series` encuentra al menos una serie con correspondencia Jellyfin/Bazarr.
- [ ] Se guarda idioma y origen, y se consulta un episodio.
- [ ] Con agente Disney+ autenticado, se valida captura de una pista accesible en un episodio y carga en Bazarr.
- [ ] Se comprueba que el subtítulo se reproduce desde Jellyfin/Moonfin.

**Límites:** el plugin no puede instalar Docker ni acceder a tu LAN por GitHub Actions. La automatización de temporadas completas no está implementada. No se admite evasión de DRM.
