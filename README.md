# Dahu

Administration tool for Windows Server RDS (Remote Desktop Services) farms.
A lightweight alternative to tools like TSAdmin or Galinette Cendrée: view the sessions, servers and profiles of a farm, and act on them.

Single `Dahu.exe` file, no installation, no runtime to deploy.

![About tab](img/about.png)

## Features

- **Sessions**: every session in the farm, with filtering, sorting and multi-selection.
  Right-click: details, processes, shadow, message, disconnect, log off, Active Directory card.
- **Servers**: hosts discovered through the Broker or declared manually, load, sessions, maintenance mode.
  Right-click: processes, message to all, restart, shutdown, mstsc, C$ share.
- **Profiles**: inventory of UPD and FSLogix disks, user resolved from the SID, size, mounted state.
- **History**: session logons and logoffs read from each server's event log.
- **Settings**: brokers, hosts, profile shares, timeouts, language, columns, alternate account.

Interface available in English and French.

## Build

```powershell
.\build.ps1
.\build.ps1 -Debug
```

The script installs the .NET 10 SDK into `%USERPROFILE%\.dotnet` if missing, without administrator rights.
