// Comandos «Lince: ejecutar» y «Lince: compilar»: guardan el archivo y corren el comando
// `lince` en la terminal integrada. Si el archivo está en una carpeta con lince.json, se
// compila el proyecto entero (eso lo decide el propio `lince`).
const vscode = require("vscode");

function correr(subcomando) {
    return async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.languageId !== "lince") {
            vscode.window.showInformationMessage("Abra un archivo .ln para ejecutarlo con Lince.");
            return;
        }

        await editor.document.save();
        const ruta = editor.document.uri.fsPath;
        const nombre = "Lince";
        const terminal = vscode.window.terminals.find((t) => t.name === nombre) ?? vscode.window.createTerminal(nombre);
        terminal.show(true);
        terminal.sendText(`lince ${subcomando} "${ruta}"`);
    };
}

function activate(contexto) {
    contexto.subscriptions.push(
        vscode.commands.registerCommand("lince.ejecutar", correr("ejecutar")),
        vscode.commands.registerCommand("lince.compilar", correr("compilar")),
    );
}

function deactivate() {}

module.exports = { activate, deactivate };
