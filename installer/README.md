# Windows installer

`build-installer.sh [version]` produces `KtcMonitoringPortal-Setup-<version>.exe` (NSIS). It publishes the portal **self-contained for win-x64** (no .NET install needed on the server) and wraps it in a setup wizard.
Build host needs the .NET 8 SDK and NSIS (`apt install nsis` / `choco install nsis`).

## What the installer does
* Copies the app to `C:\Program Files\KtcPortal` and registers the **KtcPortal Windows Service** (delayed auto-start, auto-restart on failure).
* Wizard collects: KTC base URL, SQL Server connection string, HTTP port, portal admin usernames, KTC service account (enables alerts / background refresh / scheduled reports), SMTP host, optional demo mode.
* Writes `C:\ProgramData\KtcPortal\appsettings.Production.json` (ACL: SYSTEM + Administrators only — it contains the service-account password and connection string), plus `logs\` and `keys\` (Data Protection) there.
* Adds an inbound Windows Firewall rule for the port; Start-menu shortcuts; Add/Remove Programs entry.
* **Upgrade-safe**: running a newer setup stops the service, replaces files, keeps the existing configuration, restarts. **Uninstall** removes the app/service/firewall rule but keeps `%ProgramData%\KtcPortal` (config, logs, keys).

## Silent / scripted install
```bat
KtcMonitoringPortal-Setup-1.0.0.exe /S /KTCURL=https://ktc.corp.local/ /DBCONN="Server=sql01;Database=KtcPortal;Trusted_Connection=True;TrustServerCertificate=True" /PORT=8080 /ADMIN=CORP\jdoe;CORP\asmith /SVCUSER=CORP\svc-ktc /SVCPASS=*** /SMTP=smtp.corp.local /D=D:\Apps\KtcPortal
```
(`/D=` must be last. Omit `/MOCK`; `/MOCK=1` = demo mode.) Uninstall: `"C:\Program Files\KtcPortal\Uninstall.exe" /S`.

## Notes
* The service runs as **LocalSystem**. If SQL Server uses Windows authentication, either grant the machine account (`DOMAIN\HOST$`) access to the database, or run the service under a domain account: `sc config KtcPortal obj= "DOMAIN\svc-portal" password= "***"`, and grant that account modify rights on `%ProgramData%\KtcPortal`.
* The portal listens on plain HTTP on the chosen port. For HTTPS, put it behind IIS/ARR or another reverse proxy (WebSockets enabled for SignalR), or add a Kestrel certificate to `appsettings.Production.json` (`Kestrel:Endpoints`) and set `Portal:RequireHttps` to `true`.
* To change settings later: edit `C:\ProgramData\KtcPortal\appsettings.Production.json`, then `sc stop KtcPortal && sc start KtcPortal`.
* The installer is **unsigned** — sign it (`signtool`) before distributing to avoid SmartScreen warnings.
