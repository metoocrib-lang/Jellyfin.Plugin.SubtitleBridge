# Moonfin Subtitle Bridge — Plugin Jellyfin 12.2

Complemento nativo **experimental** para Jellyfin 12.2 (.NET 10), diseñado para gestionar series e idiomas desde Dashboard → Plugins y coordinar subtítulos con Bazarr y un agente Docker separado.

[![Build Jellyfin 12.2](https://github.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/actions/workflows/build.yml/badge.svg)](https://github.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/actions/workflows/build.yml)

## Instalar desde el catálogo de Jellyfin

1. En Jellyfin 12.2: **Dashboard → Plugins → Repositories → Add Repository**.
2. Nombre: `Moonfin Subtitle Bridge`.
3. URL:
   ```text
   https://raw.githubusercontent.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/main/manifest.json
   ```
4. Guardar; **Plugins → Catalog → Moonfin Subtitle Bridge → Install**; reiniciar Jellyfin si lo solicita.
5. Abrir los ajustes del plugin. Conectar el servicio Docker auxiliar y probar comunicación.

**Paquete compilado:** [GitHub Release v0.3.1.0](https://github.com/metoocrib-lang/Jellyfin.Plugin.SubtitleBridge/releases/tag/v0.3.1.0).

**Manual de instalación, red Docker y diagnóstico:** [PRUEBAS-JELLYFIN-12.2.md](docs/PRUEBAS-JELLYFIN-12.2.md).

## Estado y alcance

- Plugin C# nativo: compilado en GitHub Actions para Jellyfin 12.2; una prueba con un contenedor Jellyfin 12.2 aislado confirmó que cargó el DLL y protegió la ruta de API (401 sin credenciales).
- Selección de series, idioma, proveedor y modo automático desde la página de configuración; estas funciones requieren que el **agente Docker** esté instalado y conectado.
- Bazarr/Sonarr: integración implementada en el prototipo del agente, pendiente de probar en el servidor del usuario.
- **Disney+ experimental:** el agente anterior requiere URL por episodio, sesión iniciada por el usuario y pistas accesibles al navegador. **No se ha validado** con la cuenta real. Tampoco existe todavía búsqueda autónoma de toda una temporada.
- Otras plataformas de streaming todavía no cuentan con extractores automáticos.
- El agente completo del prototipo v0.3 está disponible en el paquete fuente compartido previamente en la conversación; su incorporación a este repositorio está pendiente. Este repositorio **sí** distribuye el complemento DLL nativo.

## Desarrollo

```bash
dotnet build Jellyfin.Plugin.SubtitleBridge/Jellyfin.Plugin.SubtitleBridge.csproj -c Release
```

Las versiones instalables se generan por GitHub Actions y se anuncian mediante `manifest.json`. **No instales el ZIP de código fuente como plugin.**

## Seguridad y limitaciones

El plugin no necesita ni almacena credenciales de Disney+; el usuario inicia sesión directamente en el navegador privado del agente. No subas a GitHub archivos `.env`, API keys, tokens, perfiles de navegador ni cookies. Mantén los puertos Docker auxiliares accesibles únicamente dentro de tu red privada. No se eluden DRM ni controles de acceso.

GitHub Actions no tiene acceso a los contenedores privados de tu red local: para la prueba integrada de Jellyfin/Bazarr/Disney+ se necesita desplegar el agente y validar desde tu equipo.

Proyecto independiente no afiliado a Jellyfin, Moonfin, Bazarr ni plataformas de streaming.
