@echo off
setlocal
set "EXE=%~dp0src\TrueFlac.App\bin\Debug\net8.0-windows\TrueFlacPlayer.exe"
if not exist "%EXE%" (
    echo Compilando True FLAC Player...
    dotnet build "%~dp0TrueFlacPlayer.sln" --disable-build-servers --nologo
)
start "" "%EXE%" %*
