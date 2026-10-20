# Moonfin Subtitle Bridge — Jellyfin 12.2

Plugin nativo experimental para **Jellyfin 12.2 / .NET 10**. Configura series, idiomas y fuentes desde **Dashboard → Plugins**. El plugin actúa como interfaz de administración y se conecta a un agente local que coordina Sonarr/Bazarr y, experimentalmente, un navegador autenticado de Disney+.

**Estado: prototipo; pendiente de compilar y probar en un servidor real.** No publica ZIP instalables automáticamente hasta revisión.

## Compilación
```bash
dotnet build Jellyfin.Plugin.SubtitleBridge/Jellyfin.Plugin.SubtitleBridge.csproj -c Release
```

## Configuración
Tras instalar un ZIP compilado y reiniciar Jellyfin, en la configuración del complemento introduce la URL interna y el token del agente Docker existente, por ejemplo `http://moonfin-subtitle-bridge:8787`.

La clave del agente no se transmite al navegador durante las operaciones del plugin (solo se muestra en la configuración para administradores con sesión autenticada); evita compartir capturas de esa configuración. Todos los endpoints de control requieren sesión de administrador de Jellyfin.

El plugin NO instala Docker ni tiene acceso por sí mismo a tu red local. El agente experimental de Disney+ aún requiere vincular URLs de episodios y no se ha probado con una cuenta real. No se evaden DRM ni controles de acceso.

## Publicación
El repo puede compilar en privado, pero para un catálogo Jellyfin convencional deben ser accesibles públicamente tanto `manifest.json` como cada `sourceUrl`. No publiques ni crees un tag de release hasta pasar compilación y validación.

Proyecto independiente, no afiliado con Jellyfin, Moonfin ni las plataformas.