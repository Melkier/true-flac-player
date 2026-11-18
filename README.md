# True FLAC Player

Reproductor y auditor local de archivos `.flac` para Windows (.NET 8 + WPF + NAudio WASAPI). Verifica la cabecera binaria real `fLaC` (incluidos archivos con etiqueta `ID3v2`), la resolución mínima, el hash MD5 del flujo PCM decodificado y el espectro de frecuencias mediante FFT para filtrar reconversiones desde formatos con pérdida (MP3/AAC/Opus).

## Instalar o usar en Windows

Descarga el paquete ZIP en [Releases](https://github.com/Melkier/true-flac-player/releases) y extrae la carpeta completa.

- Abre `TrueFlacPlayer.exe` para iniciar el reproductor.
- Arrastra carpetas, archivos `.flac`, listas `.m3u/.m3u8` o accesos directos `.lnk` a la ventana, o pega enlaces de búsqueda e ítems de Archive.org.
- También incluye la subcarpeta `codigo-fuente/` lista para abrir o compilar.

## Desarrollo en otra PC

Clona el repositorio en VS Code con **.NET 8 SDK** en Windows:

```powershell
git clone https://github.com/Melkier/true-flac-player.git
cd true-flac-player
dotnet build TrueFlacPlayer.sln
dotnet test TrueFlacPlayer.sln
```

Para lanzar la aplicación desde el repositorio editable, ejecuta `Iniciar True FLAC Player.cmd` o `dotnet run --project src\TrueFlac.App\TrueFlac.App.csproj`. Si alternas entre equipos, haz **Pull** antes de editar y **Commit + Push** al terminar.
