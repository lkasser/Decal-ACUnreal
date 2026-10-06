# Mag-Filter

Mag-nus's Mag-Filter, a Decal network filter, from **Mag-Plugins**:
`https://github.com/Mag-nus/Mag-Plugins`, commit `32304397b1abf42eb146db4968237f3ed47a2a9d`
("Fix AutoBuySell to sell unequipped armor, inspired by #6"). Version 1.0.0.0, file version 2.0.3.

Its sources are not kept in this repository. `fetch.ps1` fetches that one commit into `upstream\`
(ignored by git), and `MagFilter.csproj` builds from it exactly the files Mag-Filter's own project
compiles - `Mag-Filter\*.cs` and seven of `Shared\` - **unchanged**. What differs is only how they
are built:

- for .NET 10, where Mag-Filter's own project targets .NET Framework 2.0 on x86;
- against this repository's stand-ins for `Decal.Adapter` and `VirindiViewService`, which Decal
  Compat gives every Decal plugin, instead of Decal's and Virindi's own;
- against `lib\VirindiPlugins\VCS5.dll` from Mag-Plugins itself, for its types alone: Mag-Filter
  writes to Virindi Chat System only when it finds it loaded, which it never is here;
- with Windows Forms from Windows' desktop runtime, for its timers.

`MagFilter.csproj` pins the commit (`MagPluginsCommit`), warns when `upstream\` is at another, and
compiles only the files it lists, so nothing upstream changes what is built without being read.

## Licence

Mag-Plugins is licensed under the **GNU Lesser General Public License 2.1** - its `license.md` -
not MIT. `Shared\VCS_Connector.cs` is VirindiPlugins' own file, under the MIT licence in its header.
Installing Mag-Filter (`tools\install-plugin.ps1 -Plugin MagFilter`) puts `license.md` beside the
DLL as `LICENSE.md`, and this README, which says where the exact sources are. They are used
unmodified; anyone may rebuild the DLL from them with `MagFilter.csproj`, and replace it.

## Passwords

Mag-Filter at this commit stores, reads and sends **no password**, and handles no credentials of
any kind: its logins choose a character - by name or by its place in the list - at the character
list, which the account is already at. The "account" it keeps beside a default character is the
account's name as the server's character list gives it, to tell accounts apart. The build stops
if any source it compiles mentions a password or credentials (`CheckMagPluginsSource`), so a later
commit that brought such a feature in could not be built without being read and left out first;
and a test checks the built DLL names neither (`MagFilterTests.NothingInItHandlesAPassword`).

## Building and installing

```powershell
third_party\Mag-Filter\fetch.ps1                 # once: Mag-Plugins at the pinned commit
dotnet build third_party\Mag-Filter\MagFilter.csproj
tools\install-plugin.ps1 -Plugin MagFilter       # fetch if need be, build, install
```

`install-plugin.ps1` puts `MagFilter.dll` in Decal Compat's own folder of Decal plugins -
`%LOCALAPPDATA%\ACHost\plugins\DecalCompat\Decal Plugins\MagFilter` - where Decal Compat finds it
with no registry entry; nothing is written to the registry. The tests build it too, once
`upstream\` is there (`tests\AC.Host.Tests\MagFilterTests.cs`).

## What it does under this host

See `docs\plugin-host.md`, "Mag-Filter". In short: its commands work; choosing the next or the
default character enters the world through the host, as `achost ctl login` does; the commands it
queues for after a login are run as typed lines; its clicks on the old client's dialogs, its
character-select frame-rate limit and its Esc-at-login shortcut have nothing to act on here.
