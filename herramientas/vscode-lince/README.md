# Lince para VS Code

Resaltado de sintaxis de los archivos `.ln`, comentarios y pares de llaves, fragmentos
(`funcion`, `si`, `para`, `clase`...) y dos comandos: **Lince: ejecutar** (Ctrl+F5, o el
botón de ejecutar del editor) y **Lince: compilar**, que corren `lince` en la terminal
integrada sobre el archivo abierto o su proyecto.

No hay todavía errores en línea ni autocompletado: eso es el servidor de lenguaje de la fase
8 de la hoja de ruta.

## Instalar

Necesita el comando `lince` en el PATH (`dotnet tool install --global --add-source ./nupkg
Lince.Cli` desde el repositorio). Después, desde PowerShell en la carpeta del repositorio:

```powershell
Copy-Item -Recurse -Force herramientas\vscode-lince "$env:USERPROFILE\.vscode\extensions\lince-0.1.0"
```

Reinicie VS Code (o «Developer: Reload Window»). Para actualizarla, repita la copia.
