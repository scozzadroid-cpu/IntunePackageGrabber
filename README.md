# IntunePackageGrabber

Tool Windows (WinForms, singolo exe) che archivia i pacchetti Win32 che Intune Management Extension (IME) scarica quando si installa un'app dal Company Portal.

IME estrae il contenuto in `C:\Windows\IMECache\<AppId>_<versione>` e lo cancella a fine installazione. Il tool sorveglia la cache, attende che l'estrazione sia completa e per ogni pacchetto salva nella cartella scelta:

- `Content\` - copia verificata (numero file e byte) del contenuto estratto
- `<Nome app>.intunewin` - rigenerato con IntuneWinAppUtil, con il file di setup letto dalla policy IME
- `app-info.txt` e `policy.json` - nome, versione, comandi di install/uninstall, detection rule e return code letti da `AppWorkload.log`

![Intune Package Grabber in ascolto con un pacchetto catturato](docs/main.png)

Il `.intunewin` originale non è recuperabile (IME lo scarica cifrato con una chiave protetta dal certificato del dispositivo): quello salvato è un pacchetto equivalente ricreato dal contenuto.

## Download

L'exe è nella pagina [Releases](../../releases/latest).

## Uso

1. Avviare `IntunePackageGrabber.exe` (richiede diritti di amministratore).
   Se serve il `.intunewin` e IntuneWinAppUtil.exe non è presente, premere **Scarica IntuneWinAppUtil**: il tool lo scarica dal [repository Microsoft](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool), ne verifica la firma Authenticode di Microsoft e lo salva in `%APPDATA%\IntunePackageGrabber\`.

   ![Pulsante Scarica IntuneWinAppUtil](docs/download-button.png)
2. Scegliere la cartella di archiviazione.
3. **Listen**, poi avviare l'installazione dal Company Portal.
4. **Stop** al termine.

Opzioni da riga di comando: `--dest <cartella>`, `--listen`, `--cache <cartella>`, `--log <file>`, `--imelogs <cartella>`, `--exit-after <secondi>`.

Impostazioni e log: `%APPDATA%\IntunePackageGrabber\`.

## Requisiti

- Windows 10/11 gestito da Intune, .NET Framework 4.8
- [IntuneWinAppUtil.exe](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) solo per generare il `.intunewin`. Viene cercato in quest'ordine: accanto all'exe, in `%APPDATA%\IntunePackageGrabber\` (pulsante di download), incorporato nell'exe.

## Build

```powershell
# Come la release: senza IntuneWinAppUtil incorporato
powershell -ExecutionPolicy Bypass -File src\build.ps1 -NoEmbed -Output .\IntunePackageGrabber.exe

# Con IntuneWinAppUtil incorporato (uso interno)
powershell -ExecutionPolicy Bypass -File src\build.ps1 -IntuneWinAppUtil <percorso>\IntuneWinAppUtil.exe -Output .\IntunePackageGrabber.exe
```

Usa il compilatore C# di .NET Framework incluso in Windows, nessun SDK necessario.

## Test

```powershell
powershell -ExecutionPolicy Bypass -File src\test.ps1 -Work C:\temp\IPG-tests -Exe .\IntunePackageGrabber.exe
```

Simula IME su una cache e un `AppWorkload.log` finti: estrazione progressiva con file aperti in scrittura, pacchetti in parallelo, cartelle rimosse durante la copia, retry, percorsi oltre 260 caratteri, setup non individuabile, pulsanti Listen/Stop.
