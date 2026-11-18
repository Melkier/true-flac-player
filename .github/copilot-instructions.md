# True FLAC Player

Abre la solución `TrueFlacPlayer.sln` en VS Code o Visual Studio con .NET 8 SDK en Windows. Consulta el `README.md` antes de editar.

- `TrueFlac.Core` contiene el inspector binario (`FlacBinaryInspector`), la aduana de validación (`FlacGatekeeper`), el analizador espectral FFT (`SpectralAnalyzer`), el motor de salida WASAPI Exclusive/Shared (`AudioPlaybackEngine`) y el cliente de Archive.org (`ArchiveOrgClient`).
- `TrueFlac.App` contiene la interfaz WPF para Windows (`MainWindow.xaml` y sus archivos parciales).
- `TrueFlac.Tests` contiene las pruebas xUnit y el generador de tramas FLAC de prueba (`TestFlacBuilder`).

Ejecuta `dotnet test TrueFlacPlayer.sln --disable-build-servers` después de cualquier cambio en el motor de validación o decodificación. Mantén fuera de Git las carpetas `bin/`, `obj/`, `releases/`, descargas de audio y documentación local de trabajo.
