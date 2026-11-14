# DevRefresh

Windows service for restoring 1C development databases from SQL Server backups. It also offers scheduled refreshes, backup analysis, and optional trimmed copies.

Download the Windows x64 installation and update packages from [GitHub Releases](https://github.com/Xelgo/devrefresh/releases). See the [deployment guide](deploy/portable/README.md) for setup and recovery instructions.

For the first upgrade to 0.2.39, use the existing offline update process. For subsequent releases, run `UPDATE-ONLINE.cmd` as a Windows administrator from the installation directory and confirm the update. The ZIP is verified before the service stops. To check the available version, run `UPDATE-ONLINE.cmd -CheckOnly`.

Shrink builds can rewrite eligible tables in the temporary database when they have at least 250,000 rows and the reduction will remove at least 70%. The service copies the rows to keep, truncates the original table, restores the saved rows, and drops the working table in the same transaction. This uses temporary space in the staging database. Set `"Shrink": { "RewriteTables": false }` in `config/server.json` and restart the service to use the original delete strategy for all tables. The optional `RewriteMinDeletePercent` setting accepts a threshold from 51 to 100.

Shrink builds commit ordinary deletes in batches of up to 50,000 rows and commit each table rewrite separately. If a build fails, DevRefresh cleans up the staging database. A new build starts with a restored full backup, and the previous published Shrink remains available until a replacement passes verification.

Build in Windows PowerShell with the .NET 10 SDK.

    .\scripts\Build-Portable.ps1 -Kind Both

When publishing a stable GitHub release tagged `vMAJOR.MINOR.PATCH`, attach the generated update ZIP under the name `DevRefresh-update-win-x64.zip`. The tag, package version, and executable version must match. Publish only the built packages and keep operational data on the instance.

Run the .NET and JavaScript tests with:

    dotnet test tests\DevRefresh.Tests\DevRefresh.Tests.csproj
    node --test

On a Windows machine with SQL Server Express LocalDB, set `DEVREFRESH_TEST_LOCALDB=1` before running the .NET tests to verify the Shrink rewrite against SQL Server.
