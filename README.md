# DevRefresh

Windows service for restoring 1C development databases from SQL Server backups. It also offers scheduled refreshes, backup analysis, and optional trimmed copies.

Download the Windows x64 installation and update packages from [GitHub Releases](https://github.com/Xelgo/devrefresh/releases). See the [deployment guide](deploy/portable/README.md) for setup and recovery instructions.

For the first upgrade to 0.2.39, use the existing offline update process. For subsequent releases, run `UPDATE-ONLINE.cmd` as a Windows administrator from the installation directory and confirm the update. The ZIP is verified before the service stops. To check the available version, run `UPDATE-ONLINE.cmd -CheckOnly`.

Build in Windows PowerShell with the .NET 10 SDK.

    .\scripts\Build-Portable.ps1 -Kind Both

When publishing a stable GitHub release tagged `vMAJOR.MINOR.PATCH`, attach the generated update ZIP under the name `DevRefresh-update-win-x64.zip`. The tag, package version, and executable version must match. Publish only the built packages and keep operational data on the instance.

Run the .NET and JavaScript tests with:

    dotnet test tests\DevRefresh.Tests\DevRefresh.Tests.csproj
    node --test
