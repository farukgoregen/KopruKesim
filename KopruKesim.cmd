@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Kopru Kesim Sunucusu
rem Bazi ortamlarda standart degiskenler eksik olabiliyor; dotnet restore icin gerekli
if not defined ProgramData set "ProgramData=%SystemDrive%\ProgramData"
if not defined ALLUSERSPROFILE set "ALLUSERSPROFILE=%ProgramData%"
if not defined PUBLIC set "PUBLIC=%SystemDrive%\Users\Public"
if not defined ProgramFiles set "ProgramFiles=%SystemDrive%\Program Files"
if not defined CommonProgramFiles set "CommonProgramFiles=%ProgramFiles%\Common Files"
set "ROOT=%~dp0"
set "WEB=%ROOT%src\KopruKesim.Web"
set "ARTIFACTS=%ROOT%veri\sunucu"
set "PANEL_PORT=5080"
set "LAN_PORT=5443"
set "LAN_FLAG="
set "BROWSER_FLAG=--tarayici"
if "%KOPRUKESIM_NO_BROWSER%"=="1" set "BROWSER_FLAG="
if not "%~2"=="" set "PANEL_PORT=%~2"
if not "%~3"=="" set "LAN_PORT=%~3"

if /I "%~1"=="yerel" goto ready
if /I "%~1"=="ag" goto network
if not "%~1"=="" goto usage
echo.
echo   KOPRU KESIM SUNUCUSU
echo   1  Yerel kullan ^(bu bilgisayar^)
echo   2  Ag modu ^(bu bilgisayar + telefon/tablet^)
echo   3  Cikis
choice /C 123 /N /M "Secim [1/2/3]: "
if errorlevel 3 exit /b 0
if errorlevel 2 goto network
if errorlevel 1 goto ready
exit /b 1

:network
set "LAN_FLAG=--ag"

:ready
where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo .NET SDK bulunamadi. .NET 8 SDK kurulu ve PATH'te olmali.
    goto failed
)
pushd "%WEB%"
if errorlevel 1 goto failed
echo Sunucu derleniyor. Ilk baslatma daha uzun surebilir.
dotnet build KopruKesim.Web.csproj --artifacts-path "%ARTIFACTS%" --disable-build-servers --nologo -m:1
if errorlevel 1 (
    popd
    goto failed
)
echo Durdurmak icin Ctrl+C.
dotnet "%ARTIFACTS%\bin\KopruKesim.Web\debug\KopruKesim.Web.dll" --panel-port "%PANEL_PORT%" --lan-port "%LAN_PORT%" %LAN_FLAG% %BROWSER_FLAG%
set "RESULT=%ERRORLEVEL%"
popd
if not "%RESULT%"=="0" goto failed
exit /b 0

:usage
echo Kullanim: KopruKesim.cmd [yerel ^| ag] [yerel_port] [ag_port]
goto failed

:failed
echo.
echo Sunucu baslatilamadi. Yukaridaki hata mesajini kontrol edin.
if "%~1"=="" pause
exit /b 1
