@echo off
rem Kopru kesim olcum arayuzunu baslatir ve tarayicida acar.
cd /d "%~dp0"
start "" http://localhost:5080
dotnet run --project src\KopruKesim.Web --urls http://localhost:5080
pause
