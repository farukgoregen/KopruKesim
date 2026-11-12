@echo off
rem Kopru kesim olcum arayuzunu AG MODUNDA baslatir: ayni Wi-Fi'deki telefon/tablet https ile baglanabilir.
rem Telefondan girilecek adres pencerede yazar (https://<bilgisayar-ip>:5443).
cd /d "%~dp0"
start "" https://localhost:5443
dotnet run --project src\KopruKesim.Web -- --ag
pause
